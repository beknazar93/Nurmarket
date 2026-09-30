using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.ViewModels;

public sealed class CustomerDisplayViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CustomerDisplayStateService _state;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _clearTimer;
    private CustomerDisplayCartSnapshot _snapshot = new();
    private bool _previewMode;
    private bool _designPreview;
    private string _statusText = "";
    private IBrush _statusBrush = Brushes.Gray;
    private DisplayPalette _palette;

    public CustomerDisplayViewModel(CustomerDisplayStateService state)
    {
        _state = state;
        Settings = UserPreferences.Instance.CustomerDisplay.Clone();
        Settings.Normalize();
        _palette = BuildPalette();
        CloseCustomerDisplayCommand = new RelayCommand(() => CloseRequested?.Invoke());
        _state.StateChanged += Refresh;
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            (_, _) => OnPropertyChanged(nameof(DateTimeText)));
        _clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer.Stop();
            _snapshot = new CustomerDisplayCartSnapshot();
            Lines.Clear();
            StatusText = "";
            RaisePresentationChanged();
        };
        _clockTimer.Start();
        Refresh();
    }

    /// <summary>Модель для живого предпросмотра в редакторе экрана покупателя (2026-09-28): свой
    /// снимок чека (образец), свои несохранённые настройки, без WebView2 (родное окно браузера не
    /// уменьшается вместе с предпросмотром) и без автоочистки после «Оплата принята».</summary>
    public static CustomerDisplayViewModel CreateDesignPreview(CustomerDisplayStateService sampleState, CustomerDisplaySettings settings)
    {
        var vm = new CustomerDisplayViewModel(sampleState) { _designPreview = true };
        vm.ApplySettings(settings);
        return vm;
    }

    /// <summary>Образец чека для предпросмотров (редактор экрана покупателя, Маркетплейс → «Виды
    /// кассы»), 2026-09-28. Сверху — последняя пробитая позиция, как в настоящем чеке.</summary>
    public static CustomerDisplayCartSnapshot BuildSampleSnapshot(bool paid)
    {
        var pcs = Tr.T("шт", "даана", "pcs", "adet", "dona");
        var kg = Tr.T("кг", "кг", "kg", "kg", "kg");
        CustomerDisplayLine Line(string title, string barcode, double qty, string unit, double price) => new()
        {
            Title = title, Barcode = barcode, Quantity = qty, Unit = unit, LineTotal = Math.Round(qty * price, 2),
        };

        return new CustomerDisplayCartSnapshot
        {
            Lines =
            [
                Line(Tr.T("Шоколад молочный 90 г", "Сүттүү шоколад 90 г", "Milk chocolate 90 g", "Sütlü çikolata 90 g", "Sutli shokolad 90 g"), "4870000000005", 2, pcs, 85),
                Line("Coca-Cola 1,5 л", "4870000000006", 3, pcs, 110),
                Line(Tr.T("Яблоки «Голден»", "Алма «Голден»", "Golden apples", "Golden elma", "«Golden» olma"), "4870000000014", 1.25, kg, 120),
                Line(Tr.T("Хлеб белый нарезной", "Кесилген ак нан", "Sliced white bread", "Dilimlenmiş beyaz ekmek", "Kesilgan oq non"), "4870000000009", 1, pcs, 35),
                Line(Tr.T("Молоко 3,2% 1 л", "Сүт 3,2% 1 л", "Milk 3.2% 1 l", "Süt %3,2 1 l", "Sut 3,2% 1 l"), "4870000000001", 2, pcs, 95),
            ],
            Subtotal = 875,
            Discount = 30,
            Total = 845,
            CashReceived = paid ? 1000 : null,
            ChangeDue = paid ? 155 : null,
        };
    }

    public CustomerDisplaySettings Settings { get; private set; }
    public ICommand CloseCustomerDisplayCommand { get; }
    public Action? CloseRequested { get; set; }
    public ObservableCollection<CustomerDisplayItemViewModel> Lines { get; } = [];
    public ObservableCollection<CustomerDisplayTableCellViewModel> TableHeaders { get; } = [];
    public string StoreName => string.IsNullOrWhiteSpace(UserPreferences.Instance.StoreName)
        ? "MARKET PLUS"
        : UserPreferences.Instance.StoreName;
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }
    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; OnPropertyChanged(); }
    }
    public string TotalText => Tr.T($"{_snapshot.Total:0.00} сом", $"{_snapshot.Total:0.00} сом", $"{_snapshot.Total:0.00} som", $"{_snapshot.Total:0.00} som", $"{_snapshot.Total:0.00} so'm");
    public string SubtotalText => Tr.T($"Промежуточный итог: {_snapshot.Subtotal:0.00} сом", $"Аралык жыйынтык: {_snapshot.Subtotal:0.00} сом",
        $"Subtotal: {_snapshot.Subtotal:0.00} som", $"Ara toplam: {_snapshot.Subtotal:0.00} som", $"Oraliq jami: {_snapshot.Subtotal:0.00} so'm");
    public string DiscountText => Tr.T($"Скидка: {_snapshot.Discount:0.00} сом", $"Арзандатуу: {_snapshot.Discount:0.00} сом",
        $"Discount: {_snapshot.Discount:0.00} som", $"İndirim: {_snapshot.Discount:0.00} som", $"Chegirma: {_snapshot.Discount:0.00} so'm");
    public string SubtotalAmountText => Tr.T($"{_snapshot.Subtotal:0.00} сом", $"{_snapshot.Subtotal:0.00} сом", $"{_snapshot.Subtotal:0.00} som", $"{_snapshot.Subtotal:0.00} som", $"{_snapshot.Subtotal:0.00} so'm");
    public string DiscountAmountText => Tr.T($"-{_snapshot.Discount:0.00} сом", $"-{_snapshot.Discount:0.00} сом", $"-{_snapshot.Discount:0.00} som", $"-{_snapshot.Discount:0.00} som", $"-{_snapshot.Discount:0.00} so'm");
    public bool HasCashInfo => _snapshot.CashReceived.HasValue && Settings.ShowChangeAfterPayment &&
        (Settings.ShowPaidAmount || Settings.ShowChange);
    public bool ShowPaidAmountColumn => HasCashInfo && Settings.ShowPaidAmount;
    public bool ShowChangeColumn => HasCashInfo && Settings.ShowChange;
    public string CashReceivedText => Tr.T($"{_snapshot.CashReceived ?? 0:0.00} сом", $"{_snapshot.CashReceived ?? 0:0.00} сом", $"{_snapshot.CashReceived ?? 0:0.00} som", $"{_snapshot.CashReceived ?? 0:0.00} som", $"{_snapshot.CashReceived ?? 0:0.00} so'm");
    public string ChangeDueText => Tr.T($"{_snapshot.ChangeDue ?? 0:0.00} сом", $"{_snapshot.ChangeDue ?? 0:0.00} сом", $"{_snapshot.ChangeDue ?? 0:0.00} som", $"{_snapshot.ChangeDue ?? 0:0.00} som", $"{_snapshot.ChangeDue ?? 0:0.00} so'm");
    public string ItemCountText => Tr.T($"{Lines.Count} поз.", $"{Lines.Count} позиция",
        $"{Lines.Count} items", $"{Lines.Count} kalem", $"{Lines.Count} ta mahsulot");
    public string DateTimeText => DateTime.Now.ToString("dd.MM.yyyy  HH:mm");
    public bool IsEmpty => Lines.Count == 0;
    public bool HasItems => !IsEmpty;
    public bool IsCardLayout => Settings.LayoutMode == CustomerDisplayLayoutMode.Cards;
    public bool IsTableLayout => !IsCardLayout;
    public bool ShowRightAdvertisement => Settings.AdvertisementEnabled && HasItems &&
        Settings.AdvertisementPosition == CustomerDisplayAdvertisementPosition.Right;
    public bool ShowBottomAdvertisement => Settings.AdvertisementEnabled && HasItems &&
        Settings.AdvertisementPosition == CustomerDisplayAdvertisementPosition.Bottom;
    public bool ShowEmptyAdvertisement => Settings.AdvertisementEnabled &&
        Settings.AdvertisementPosition == CustomerDisplayAdvertisementPosition.EmptyScreen;
    public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(Settings.BackgroundImagePath);
    public string InformationImagePath
    {
        get
        {
            if (HasPaymentQr)
            {
                var preferences = UserPreferences.Instance;
                // Банк, реально выбранный кассиром в диалоге оплаты (2026-09-21) — раньше здесь
                // всегда брался первый попавшийся загруженный QR из списка банков и не менялся
                // при выборе другого банка. См. CheckoutViewModel.SelectedBank/ICustomerDisplayService.
                var selectedBankQr = _state.SelectedBankQrPath;
                string? staticQr = null;
                if (!string.IsNullOrWhiteSpace(selectedBankQr) && File.Exists(selectedBankQr))
                    staticQr = selectedBankQr;
                if (staticQr is null)
                {
                    var bankQr = preferences.BankQrPaths?.Values
                        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
                    if (!string.IsNullOrWhiteSpace(bankQr))
                        staticQr = bankQr;
                }
                if (staticQr is null
                    && !string.IsNullOrWhiteSpace(preferences.QrCodePath)
                    && File.Exists(preferences.QrCodePath))
                {
                    staticQr = preferences.QrCodePath;
                }

                if (staticQr is not null)
                {
                    // Если владелец включил динамический QR — подставляем код с уже вписанной
                    // суммой чека, чтобы покупателю не набирать её руками. Любая осечка
                    // (не распознали картинку, это не ELQR, нулевой чек) возвращает null,
                    // и ниже показывается тот же статический QR, что и раньше.
                    if (preferences.DynamicPaymentQrEnabled && _snapshot.Total > 0 && !_designPreview)
                    {
                        var dynamicQr = DynamicPaymentQrService.TryBuildDynamicQrImage(
                            staticQr, (decimal)_snapshot.Total);
                        if (!string.IsNullOrWhiteSpace(dynamicQr))
                            return dynamicQr;
                    }

                    return staticQr;
                }
            }
            if (Settings.AdvertisementEnabled &&
                !string.IsNullOrWhiteSpace(Settings.AdvertisementImagePath) &&
                File.Exists(Settings.AdvertisementImagePath))
                return Settings.AdvertisementImagePath;
            return "";
        }
    }
    public bool HasInformationImage => !string.IsNullOrWhiteSpace(InformationImagePath);

    private static readonly string[] AnimatedMediaExtensions = [".gif", ".mp4", ".webm"];

    /// <summary>
    /// true, если текущее изображение — анимированный GIF или видео. Avalonia.Image не умеет
    /// проигрывать ни то, ни другое (показывает только первый кадр), поэтому такие файлы
    /// рендерятся через встроенный WebView2 (см. CustomerInfoCard.axaml.cs), а не Image.
    /// В предпросмотре редактора WebView2 нет — там показывается первый кадр.
    /// </summary>
    public bool IsAnimatedInformationMedia =>
        !_designPreview &&
        HasInformationImage &&
        AnimatedMediaExtensions.Contains(Path.GetExtension(InformationImagePath).ToLowerInvariant());

    public bool IsStaticInformationImage => HasInformationImage && !IsAnimatedInformationMedia;
    // Пока в чеке есть товары, справа показываем QR для оплаты (актуальное действие для покупателя);
    // когда экран простаивает (корзина пуста), уступаем место рекламе, если она настроена.
    // 2026-09-28: QR можно скрыть в редакторе экрана покупателя («Показывать: QR оплаты»).
    public bool HasPaymentQr => HasItems && Settings.ShowPaymentQr &&
        ((UserPreferences.Instance.BankQrPaths?.Values.Any(path =>
             !string.IsNullOrWhiteSpace(path) && File.Exists(path)) ?? false) ||
        (!string.IsNullOrWhiteSpace(UserPreferences.Instance.QrCodePath) &&
         File.Exists(UserPreferences.Instance.QrCodePath)));
    public string InformationEyebrow => HasPaymentQr ? Tr.T("ОПЛАТА ПО QR", "QR АРКЫЛУУ ТӨЛӨӨ", "PAY BY QR", "QR İLE ÖDEME", "QR ORQALI TO'LOV") : Tr.T("ИНФОРМАЦИЯ", "МААЛЫМАТ", "INFORMATION", "BİLGİ", "MA'LUMOT");
    public string InformationTitle => HasPaymentQr
        ? Tr.T("Наведите камеру", "Камераны багыттаңыз", "Point your camera", "Kamerayı yöneltin", "Kamerani yo'naltiring")
        : string.IsNullOrWhiteSpace(Settings.AdvertisementTitle)
            ? Tr.T("Полезная информация", "Пайдалуу маалымат", "Useful information", "Yararlı bilgiler", "Foydali ma'lumot")
            : Settings.AdvertisementTitle;
    public string InformationDescription => HasPaymentQr
        ? Tr.T($"После подтверждения кассиром оплатите {_snapshot.Total:0.00} сом", $"Кассир ырастагандан кийин {_snapshot.Total:0.00} сом төлөңүз",
            $"After the cashier confirms, pay {_snapshot.Total:0.00} som", $"Kasiyer onayladıktan sonra {_snapshot.Total:0.00} som ödeyin",
            $"Kassir tasdiqlaganidan keyin {_snapshot.Total:0.00} so'm to'lang")
        : string.IsNullOrWhiteSpace(Settings.AdvertisementDescription)
            ? Tr.T("Здесь может отображаться акция, реклама или QR-код оплаты.", "Бул жерде акция, жарнама же төлөм QR-коду көрсөтүлүшү мүмкүн.", "A promotion, advertisement, or payment QR code can be shown here.", "Burada bir promosyon, reklam veya ödeme QR kodu gösterilebilir.", "Bu yerda aksiya, reklama yoki to'lov QR-kodi ko'rsatilishi mumkin.")
            : Settings.AdvertisementDescription;
    public string ScannerStatusText => _state.CurrentStatus switch
    {
        CustomerDisplayPaymentStatus.Processing => Tr.T("Обработка оплаты", "Төлөм иштелүүдө", "Processing payment", "Ödeme işleniyor", "To'lov qayta ishlanmoqda"),
        CustomerDisplayPaymentStatus.Success => Tr.T("Оплата принята", "Төлөм кабыл алынды", "Payment accepted", "Ödeme kabul edildi", "To'lov qabul qilindi"),
        CustomerDisplayPaymentStatus.Failed => Tr.T("Оплата не прошла", "Төлөм өтпөй калды", "Payment failed", "Ödeme başarısız oldu", "To'lov amalga oshmadi"),
        _ => Tr.T("Сканер готов", "Сканер даяр", "Scanner ready", "Barkod okuyucu hazır", "Skaner tayyor"),
    };
    public string ScannerStatusDescription => _state.CurrentStatus switch
    {
        CustomerDisplayPaymentStatus.Processing => Tr.T("Пожалуйста, подождите", "Күтө туруңуз", "Please wait", "Lütfen bekleyin", "Iltimos, kuting"),
        // 2026-09-28: текст «Спасибо за покупку» задаётся в редакторе экрана покупателя.
        CustomerDisplayPaymentStatus.Success => SuccessMessage,
        CustomerDisplayPaymentStatus.Failed => Tr.T("Обратитесь к кассиру", "Кассирге кайрылыңыз", "Please see the cashier", "Kasiyere başvurun", "Kassirga murojaat qiling"),
        _ => Tr.T("Можно сканировать следующий товар", "Кийинки товарды сканерлесе болот", "You can scan the next product", "Sıradaki ürünü okutabilirsiniz", "Keyingi mahsulotni skanerlash mumkin"),
    };
    public bool IsPreviewMode => _previewMode;
    public bool IsDesignPreview => _designPreview;
    public CornerRadius DisplayCornerRadius => new(Settings.CornerRadius);
    public Thickness PageMargin => new(24 * Settings.Scale);
    public double HeaderFontSize => 24 * Settings.Scale;
    public double BodyFontSize => 18 * Settings.Scale;
    public double EmptyTitleFontSize => 36 * Settings.Scale;
    // 2026-09-28: размер итоговой суммы настраивается отдельно (редактор экрана покупателя).
    public double TotalFontSize => 38 * Settings.Scale * Settings.TotalScale;
    public double CardWidth => Settings.CardColumns switch
    {
        CustomerDisplayColumnMode.Two => 380 * Settings.CardSize,
        CustomerDisplayColumnMode.Three => 300 * Settings.CardSize,
        CustomerDisplayColumnMode.Four => 240 * Settings.CardSize,
        _ => 280 * Settings.CardSize,
    };
    public double CardHeight => 225 * Settings.CardSize;

    // ------------------------------------------------------------------ вид экрана (2026-09-28)

    /// <summary>Вид экрана покупателя: «как у кассы» — тот же, что сейчас у кассы
    /// (UserPreferences.MainLayoutMode), иначе выбранный в редакторе.</summary>
    public string EffectiveStyle => ResolveStyle(Settings.DisplayStyle);

    public static string ResolveStyle(string? displayStyle) =>
        string.IsNullOrWhiteSpace(displayStyle)
        || string.Equals(displayStyle, CustomerDisplaySettings.StyleAuto, StringComparison.OrdinalIgnoreCase)
            ? KassaLayouts.Normalize(UserPreferences.Instance.MainLayoutMode)
            : KassaLayouts.Normalize(displayStyle);

    /// <summary>Надпись сверху: своя из редактора или название магазина.</summary>
    public string HeaderTitle => string.IsNullOrWhiteSpace(Settings.StoreTitle) ? StoreName : Settings.StoreTitle.Trim();
    public string HeaderMonogram
    {
        get
        {
            var words = HeaderTitle.Split(' ', '-', '«', '»', '"').Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0])).ToArray();
            return words.Length switch
            {
                0 => "N",
                1 => words[0][..1].ToUpperInvariant(),
                _ => string.Concat(words[0][..1], words[1][..1]).ToUpperInvariant(),
            };
        }
    }
    public string Tagline => Tr.T("Ваши покупки — быстро и удобно", "Сатып алууларыңыз — тез жана ыңгайлуу",
        "Your purchases — fast and easy", "Alışverişiniz — hızlı ve kolay", "Xaridlaringiz — tez va qulay");
    public bool ShowClock => Settings.ShowDateTime;
    public string ClockText => DateTime.Now.ToString("HH:mm");
    public string DateText => DateTime.Now.ToString("dd.MM.yyyy");
    /// <summary>Список позиций показан (есть позиции и он не скрыт в редакторе).</summary>
    public bool ShowLines => Settings.ShowItemList && HasItems;
    /// <summary>Список скрыт в редакторе, а позиции есть — вместо списка крупная сумма.</summary>
    public bool ShowTotalOnly => !Settings.ShowItemList && HasItems;
    public CustomerDisplayItemViewModel? LastLine => Lines.Count > 0 ? Lines[0] : null;
    public bool HasLastLine => Lines.Count > 0;
    public string LastAddedCaption => Tr.T("ТОЛЬКО ЧТО ДОБАВЛЕНО", "ЖАҢЫ ЭЛЕ КОШУЛДУ", "JUST ADDED", "YENİ EKLENDİ", "HOZIRGINA QO'SHILDI");
    public string TotalNumberText => Money(_snapshot.Total);
    public string SubtotalNumberText => Money(_snapshot.Subtotal);
    public string DiscountNumberText => "−" + Money(_snapshot.Discount);
    public string CashReceivedNumberText => Money(_snapshot.CashReceived ?? 0);
    public string ChangeDueNumberText => Money(_snapshot.ChangeDue ?? 0);
    public bool HasDiscount => _snapshot.Discount > 0.004;
    public string CurrencyText => Tr.T("сом", "сом", "som", "som", "so'm");
    public string TotalDueLabel => Tr.T("К оплате", "Төлөөгө", "To pay", "Ödenecek", "To'lovga");
    public string TotalLabel => Tr.T("Итого", "Жыйынтык", "Total", "Toplam", "Jami");
    public string SubtotalLabel => Tr.T("Промежуточный итог", "Аралык жыйынтык", "Subtotal", "Ara toplam", "Oraliq jami");
    public string DiscountLabel => Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma");
    public string ReceivedLabel => Tr.T("Получено", "Алынды", "Received", "Alınan", "Qabul qilindi");
    public string ChangeLabel => Tr.T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim");
    /// <summary>«Сдача: 155.00 сом».</summary>
    public string ChangeLine => $"{ChangeLabel}: {ChangeDueNumberText} {CurrencyText}";
    public double BigTotalFontSize => 60 * Settings.Scale * Settings.TotalScale;
    public double HugeTotalFontSize => 104 * Settings.Scale * Settings.TotalScale;
    public double CurrencyFontSize => Math.Max(14, 22 * Settings.Scale * Math.Sqrt(Settings.TotalScale));

    /// <summary>Приветствие на пустом экране. Текст по умолчанию — на языке программы.</summary>
    public string GreetingTitle => IsDefaultText(Settings.EmptyTitle, CustomerDisplaySettings.DefaultEmptyTitle)
        ? Tr.T("Добро пожаловать!", "Кош келиңиз!", "Welcome!", "Hoş geldiniz!", "Xush kelibsiz!")
        : Settings.EmptyTitle.Trim();
    public string GreetingDescription => IsDefaultText(Settings.EmptyDescription, CustomerDisplaySettings.DefaultEmptyDescription)
        ? Tr.T("Ваши покупки появятся на этом экране", "Сатып алууларыңыз ушул экранда көрүнөт",
            "Your purchases will appear on this screen", "Alışverişiniz bu ekranda görünecek", "Xaridlaringiz shu ekranda ko'rinadi")
        : Settings.EmptyDescription.Trim();
    public string SuccessMessage => IsDefaultText(Settings.SuccessText, CustomerDisplaySettings.DefaultSuccessText)
        ? Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışverişiniz için teşekkürler!", "Xaridingiz uchun rahmat!")
        : Settings.SuccessText.Trim();

    public bool IsProcessing => _state.CurrentStatus == CustomerDisplayPaymentStatus.Processing;
    public bool IsPaymentSuccess => _state.CurrentStatus == CustomerDisplayPaymentStatus.Success;
    public bool IsPaymentFailed => _state.CurrentStatus == CustomerDisplayPaymentStatus.Failed;
    public bool HasPaymentStatus => _state.CurrentStatus != CustomerDisplayPaymentStatus.Idle;
    /// <summary>Есть что показать в блоке QR / рекламы (в «Классике» блок стоит всегда, как раньше).</summary>
    public bool HasInformationContent => HasInformationImage || Settings.AdvertisementEnabled;
    /// <summary>Блок QR / рекламы в новых видах: после «Оплата принята» QR уже не нужен — его место
    /// занимает сообщение об оплате и сдача (на небольшом заднем экране всё сразу не помещается).</summary>
    public bool ShowInformationCard => HasInformationContent && !IsPaymentSuccess;
    public bool ShowPaymentQrCard => HasPaymentQr && !IsPaymentSuccess;

    private static bool IsDefaultText(string? value, string defaultText) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), defaultText, StringComparison.Ordinal);

    private static readonly NumberFormatInfo MoneyFormat = new()
    {
        NumberDecimalSeparator = ".",
        NumberGroupSeparator = " ",
        NumberGroupSizes = [3],
    };

    /// <summary>«1 234.50» — как суммы в раскладках кассы (MoneyConverter).</summary>
    internal static string Money(double amount) => amount.ToString("#,0.00", MoneyFormat);

    /// <summary>«Системная» тема экрана покупателя (2026-09-07, исправление реального бага):
    /// раньше все брaши ниже проверяли только Settings.Theme == Dark, поэтому пункт "Системная"
    /// в Настройки → Монитор ничего не делал — экран покупателя всегда оставался светлым, даже
    /// когда касса переключена в тёмную тему. Теперь "Системная" реально следует за
    /// UserPreferences.Instance.DarkTheme, включая живое обновление — см. App.ApplyTheme,
    /// который дёргает AvaloniaCustomerDisplayService.ApplySettings на каждое переключение.</summary>
    private bool IsDarkEffective => Settings.Theme switch
    {
        CustomerDisplayTheme.Dark => true,
        CustomerDisplayTheme.Light => false,
        _ => UserPreferences.Instance.DarkTheme,
    };

    // ------------------------------------------------------------------ цвета

    public IBrush AccentBrush => _palette.Accent;
    public IBrush AccentSoftBrush => _palette.AccentSoft;
    public IBrush BackgroundBrush => _palette.Background;
    public IBrush SurfaceBrush => _palette.Surface;
    public IBrush SurfaceAltBrush => _palette.SurfaceAlt;
    public IBrush TextBrush => _palette.Text;
    public IBrush SecondaryTextBrush => _palette.TextSoft;
    public IBrush BorderBrushValue => _palette.Border;
    public IBrush DiscountBrush => _palette.Danger;
    public IBrush DiscountSoftBrush => _palette.DangerSoft;
    public IBrush ScannerStatusBrush => _state.CurrentStatus switch
    {
        CustomerDisplayPaymentStatus.Processing => Brush("#F59E0B", "#F59E0B"),
        CustomerDisplayPaymentStatus.Success => Brush("#10B981", "#10B981"),
        CustomerDisplayPaymentStatus.Failed => Brush("#EF4444", "#EF4444"),
        _ => Brush("#10B981", "#10B981"),
    };
    public IBrush ScannerStatusSoftBrush => _state.CurrentStatus switch
    {
        CustomerDisplayPaymentStatus.Processing => WithAlpha("#F59E0B", 32),
        CustomerDisplayPaymentStatus.Success => WithAlpha("#10B981", 32),
        CustomerDisplayPaymentStatus.Failed => WithAlpha("#EF4444", 32),
        _ => WithAlpha("#10B981", 32),
    };

    /// <summary>Все цвета экрана разом — CustomerDisplayView кладёт их в свои ресурсы
    /// (CustomerDisplay*Brush), откуда их берут виды экрана и шаблоны строк чека.</summary>
    internal IReadOnlyDictionary<string, IBrush> PaletteResources => _palette.Resources;

    private double _ornamentBandHeight;

    /// <summary>Высота полос орнамента сверху и снизу экрана (тема «Кыргыз», 2026-09-30); 0 — нет.</summary>
    public double OrnamentBandHeight => _ornamentBandHeight;

    /// <summary>Тёмный ли экран сейчас (вид «Профи» всегда тёмный).</summary>
    public bool IsDarkDisplay => _palette.Dark;

    public event EventHandler? PresentationChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplySettings(CustomerDisplaySettings settings)
    {
        Settings = settings.Clone();
        Settings.Normalize();
        OnPropertyChanged(nameof(Settings));
        RaiseThemeBrushesChanged();
        Refresh();
    }

    /// <summary>Вид или тема кассы поменялись (App.ApplyMainLayoutMode / ApplyAccentTheme,
    /// 2026-09-28): вид «как у кассы» и цвета «как тема кассы» пересчитываются, открытый экран
    /// перестраивается.</summary>
    public void RefreshPresentation()
    {
        RaiseThemeBrushesChanged();
        Refresh();
    }

    /// <summary>Экран покупателя обычно остаётся открытым на втором мониторе весь день, а
    /// {Binding SurfaceBrush} и т.п. — вычисляемые свойства, не поля: без явного PropertyChanged
    /// на КАЖДОЕ из них смена темы кассы (или ручная смена темы экрана покупателя в Настройки →
    /// Монитор) не перекрашивала бы уже открытое окно, только следующее его открытие.</summary>
    private void RaiseThemeBrushesChanged()
    {
        _palette = BuildPalette();
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(AccentSoftBrush));
        OnPropertyChanged(nameof(BackgroundBrush));
        OnPropertyChanged(nameof(SurfaceBrush));
        OnPropertyChanged(nameof(SurfaceAltBrush));
        OnPropertyChanged(nameof(TextBrush));
        OnPropertyChanged(nameof(SecondaryTextBrush));
        OnPropertyChanged(nameof(BorderBrushValue));
    }

    public void SetPreviewMode(bool enabled)
    {
        _previewMode = enabled;
        OnPropertyChanged(nameof(IsPreviewMode));
        Refresh();
    }

    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        _snapshot = _state.CurrentSnapshot;
        var columns = GetVisibleColumns();
        Lines.Clear();
        TableHeaders.Clear();
        foreach (var column in columns)
            TableHeaders.Add(new CustomerDisplayTableCellViewModel(ColumnLabel(column), ColumnWidth(column)));
        var number = _snapshot.Lines.Count;
        foreach (var line in _snapshot.Lines)
            Lines.Add(new CustomerDisplayItemViewModel(line, columns) { Number = number--, ShowPhoto = Settings.ShowProductImage });

        if (_previewMode && Lines.Count == 0)
        {
            Lines.Add(new CustomerDisplayItemViewModel(Tr.T("Кофе натуральный", "Табигый кофе", "Ground coffee", "Öğütülmüş kahve", "Tabiiy qahva"), "4601234567890", 2, Tr.T("шт", "даана", "pcs", "adet", "dona"), 320, columns) { Number = 2 });
            Lines.Add(new CustomerDisplayItemViewModel(Tr.T("Молоко 1 л", "Сүт 1 л", "Milk 1 L", "Süt 1 L", "Sut 1 l"), "4870123456789", 1, Tr.T("шт", "даана", "pcs", "adet", "dona"), 95, columns) { Number = 1 });
            _snapshot = new CustomerDisplayCartSnapshot { Subtotal = 415, Discount = 15, Total = 400 };
        }

        (StatusText, StatusBrush) = _state.CurrentStatus switch
        {
            CustomerDisplayPaymentStatus.Processing => (_state.StatusMessage ?? Tr.T("Идёт оплата…", "Төлөм жүрүп жатат…", "Processing payment…", "Ödeme işleniyor…", "To'lov amalga oshirilmoqda…"), Brushes.DarkOrange),
            CustomerDisplayPaymentStatus.Success => (_state.StatusMessage ?? SuccessMessage, Brushes.DarkGreen),
            CustomerDisplayPaymentStatus.Failed => (_state.StatusMessage ?? Tr.T("Ошибка оплаты", "Төлөм катасы", "Payment error", "Ödeme hatası", "To'lov xatosi"), Brushes.DarkRed),
            _ => ("", SecondaryTextBrush),
        };

        _clearTimer.Stop();
        if (_state.CurrentStatus == CustomerDisplayPaymentStatus.Success && Settings.ClearDelaySeconds > 0 && !_designPreview)
        {
            _clearTimer.Interval = TimeSpan.FromSeconds(Settings.ClearDelaySeconds);
            _clearTimer.Start();
        }
        RaisePresentationChanged();
    }

    private void RaisePresentationChanged()
    {
        foreach (var property in typeof(CustomerDisplayViewModel).GetProperties()
                     .Where(property => property.GetIndexParameters().Length == 0))
            OnPropertyChanged(property.Name);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<string> GetVisibleColumns() =>
        Settings.TableColumnOrder.Where(column => column switch
        {
            "Product" => Settings.ShowTableProduct,
            "Quantity" => Settings.ShowTableQuantity,
            "Price" => Settings.ShowTablePrice,
            "Discount" => Settings.ShowTableDiscount,
            "Total" => Settings.ShowTableTotal,
            "Barcode" => Settings.ShowTableBarcode,
            _ => false,
        }).ToArray();

    private static string ColumnLabel(string key) => key switch
    {
        "Product" => Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), "Quantity" => Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), "Price" => Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx"),
        "Discount" => Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma"), "Total" => Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"), "Barcode" => Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), _ => key,
    };
    private static double ColumnWidth(string key) => key == "Product" ? 280 : key == "Barcode" ? 180 : 145;

    // ------------------------------------------------------------------ палитра (2026-09-28)

    private sealed class DisplayPalette
    {
        public required bool Dark { get; init; }
        public required IBrush Background { get; init; }
        public required IBrush Surface { get; init; }
        public required IBrush SurfaceAlt { get; init; }
        public required IBrush Border { get; init; }
        public required IBrush Text { get; init; }
        public required IBrush TextSoft { get; init; }
        public required IBrush Accent { get; init; }
        public required IBrush AccentSoft { get; init; }
        public required IBrush Danger { get; init; }
        public required IBrush DangerSoft { get; init; }
        public required IReadOnlyDictionary<string, IBrush> Resources { get; init; }
    }

    /// <summary>Цвета экрана. «Как тема кассы» — ключевые цвета текущей темы кассы (встроенной
    /// или своей из редактора тем) для светлого/тёмного варианта экрана; «свои» — фон, акцент и
    /// текст из настроек, остальное как было до 2026-09-28 (белые / тёмно-синие панели). Остальные
    /// оттенки выводятся смешением, как в AccentThemeService. Вид «Профи» всегда тёмный.</summary>
    private DisplayPalette BuildPalette()
    {
        var forceDark = EffectiveStyle == KassaLayouts.Pro;
        var dark = forceDark || IsDarkEffective;
        string background, surface, surfaceAlt, border, text, textSoft, accent, accentText, success, danger, warning;

        if (Settings.UseThemeColors != false)
        {
            CustomThemeColors c;
            try { c = AccentThemeService.GetThemeColors(UserPreferences.Instance.AccentTheme, dark); }
            catch (Exception) { c = new CustomThemeColors(); }
            background = Hex(c.Background, dark ? "#0B1220" : "#EFF3F8");
            surface = Hex(c.Panel, dark ? "#18202D" : "#FFFFFF");
            text = Hex(c.Text, dark ? "#FFFFFF" : "#0F172A");
            textSoft = Hex(c.TextSoft, dark ? "#CBD5E1" : "#64748B");
            accent = Hex(c.Accent, "#FACC15");
            accentText = Hex(c.AccentText, BestTextOn(accent));
            success = Hex(c.Success, dark ? "#34D399" : "#047857");
            danger = Hex(c.Danger, dark ? "#F14C4C" : "#B91C1C");
            warning = Hex(c.Warning, dark ? "#D7BA7D" : "#B45309");
            surfaceAlt = Mix(surface, background, 0.55);
            border = Mix(surface, text, dark ? 0.2 : 0.13);
        }
        else
        {
            background = IsDarkEffective && string.Equals(Settings.BackgroundColor, "#F8FAFC", StringComparison.OrdinalIgnoreCase)
                ? "#0B1220"
                : Hex(Settings.BackgroundColor, "#F8FAFC");
            if (forceDark && AccentThemeService.Contrast(background, "#000000") > AccentThemeService.Contrast(background, "#FFFFFF"))
                background = "#0B1220";
            surface = dark ? "#18202D" : "#FFFFFF";
            surfaceAlt = dark ? "#222C3A" : "#F8FAFC";
            border = dark ? "#334155" : "#E2E8F0";
            text = dark ? "#FFFFFF" : "#0F172A";
            textSoft = dark ? "#CBD5E1" : "#64748B";
            // Свой цвет текста — только если он читается на панелях (иначе, например, тёмный текст
            // в тёмном «Профи» пропал бы совсем).
            if (TryHex(Settings.TextColor) is { } customText && AccentThemeService.Contrast(customText, surface) >= 3)
            {
                text = customText;
                textSoft = Mix(customText, surface, 0.35);
            }
            accent = Hex(Settings.AccentColor, "#FACC15");
            accentText = BestTextOn(accent);
            success = dark ? "#34D399" : "#047857";
            danger = "#E11D48";
            warning = dark ? "#FBBF24" : "#B45309";
        }

        var accentSoft = Mix(accent, surface, dark ? 0.74 : 0.84);
        var successSoft = Mix(success, surface, dark ? 0.78 : 0.86);
        var dangerSoft = Mix(danger, surface, dark ? 0.78 : 0.88);
        var header = dark ? Mix(background, "#000000", 0.35) : "#111827";

        var hex = new Dictionary<string, string>
        {
            ["CustomerDisplayBackgroundBrush"] = background,
            ["CustomerDisplaySurfaceBrush"] = surface,
            ["CustomerDisplaySurfaceAltBrush"] = surfaceAlt,
            ["CustomerDisplayBorderBrush"] = border,
            ["CustomerDisplayTextBrush"] = text,
            ["CustomerDisplaySecondaryTextBrush"] = textSoft,
            ["CustomerDisplayAccentBrush"] = accent,
            ["CustomerDisplayAccentSoftBrush"] = accentSoft,
            ["CustomerDisplayAccentForegroundBrush"] = accentText,
            // Акцент, доведённый до читаемого контраста на панели (жёлтый → коричневатый), — для
            // цен и подписей; сам акцент остаётся для заливок.
            ["CustomerDisplayAccentInkBrush"] = Readable(accent, surface, text, 3.0),
            ["CustomerDisplaySuccessBrush"] = success,
            ["CustomerDisplaySuccessSoftBrush"] = successSoft,
            ["CustomerDisplaySuccessForegroundBrush"] = BestTextOn(success),
            ["CustomerDisplaySuccessInkBrush"] = Readable(success, successSoft, text, 3.0),
            ["CustomerDisplayDiscountBrush"] = Readable(danger, surface, text, 3.0),
            ["CustomerDisplayDiscountSoftBrush"] = dangerSoft,
            ["CustomerDisplayWarningInkBrush"] = Readable(warning, surface, text, 3.0),
            ["CustomerDisplayStripBrush"] = Mix(warning, surface, dark ? 0.82 : 0.9),
            ["CustomerDisplayStripBorderBrush"] = Mix(warning, surface, dark ? 0.5 : 0.6),
            ["CustomerDisplayHeaderBrush"] = header,
            ["CustomerDisplayHeaderTextBrush"] = "#FFFFFF",
            ["CustomerDisplayHeaderSoftTextBrush"] = Mix("#FFFFFF", header, 0.35),
        };
        var resources = hex.ToDictionary(pair => pair.Key, pair => (IBrush)new SolidColorBrush(Color.Parse(pair.Value)));

        // 2026-09-30, владелец: «экран покупателя тоже должен меняться в национальный вид». У темы
        // с орнаментом («Кыргыз») при цветах «как тема кассы» фон, панели, акцент и шапка — узором,
        // сверху и снизу — полоса орнамента (CustomerDisplayView). Основа плиток = цвета палитры.
        IBrush? band = null;
        if (Settings.UseThemeColors != false)
        {
            var theme = UserPreferences.Instance.AccentTheme;
            void Pattern(string part, params string[] keys)
            {
                if (AccentThemeService.GetOrnamentBrush(theme, dark, part) is { } brush)
                    foreach (var key in keys)
                        resources[key] = brush;
            }
            Pattern("bg", "CustomerDisplayBackgroundBrush");
            Pattern("panel", "CustomerDisplaySurfaceBrush");
            Pattern("soft", "CustomerDisplaySurfaceAltBrush");
            Pattern("accent", "CustomerDisplayAccentBrush");
            if (!dark)
                Pattern("accent", "CustomerDisplayHeaderBrush");
            band = AccentThemeService.GetOrnamentBrush(theme, dark, "band");
        }
        resources["CustomerDisplayOrnamentBrush"] = band ?? Brushes.Transparent;
        _ornamentBandHeight = band is null ? 0 : 28;

        return new DisplayPalette
        {
            Dark = dark,
            Background = resources["CustomerDisplayBackgroundBrush"],
            Surface = resources["CustomerDisplaySurfaceBrush"],
            SurfaceAlt = resources["CustomerDisplaySurfaceAltBrush"],
            Border = resources["CustomerDisplayBorderBrush"],
            Text = resources["CustomerDisplayTextBrush"],
            TextSoft = resources["CustomerDisplaySecondaryTextBrush"],
            Accent = resources["CustomerDisplayAccentBrush"],
            AccentSoft = resources["CustomerDisplayAccentSoftBrush"],
            Danger = new SolidColorBrush(Color.Parse(danger)),
            DangerSoft = resources["CustomerDisplayDiscountSoftBrush"],
            Resources = resources,
        };
    }

    private static string Hex(string? value, string fallback) => TryHex(value) ?? fallback;

    private static string? TryHex(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Color.TryParse(value.Trim(), out _) ? value.Trim() : null;

    private static string Mix(string a, string b, double t) => AccentThemeService.MixHex(a, b, t);

    private static string BestTextOn(string background) =>
        AccentThemeService.Contrast(background, "#FFFFFF") >= AccentThemeService.Contrast(background, "#111111") ? "#FFFFFF" : "#111111";

    /// <summary>Цвет, доведённый смешением с цветом текста до нужного контраста на фоне.</summary>
    private static string Readable(string color, string background, string text, double minContrast)
    {
        for (var t = 0.0; t <= 1.0; t += 0.1)
        {
            var candidate = Mix(color, text, t);
            if (AccentThemeService.Contrast(candidate, background) >= minContrast)
                return candidate;
        }
        return text;
    }

    private static IBrush Brush(string value, string fallback)
    {
        try { return new SolidColorBrush(Color.Parse(value)); }
        catch { return new SolidColorBrush(Color.Parse(fallback)); }
    }

    private static IBrush WithAlpha(string value, byte alpha)
    {
        try
        {
            var color = Color.Parse(value);
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }
        catch { return new SolidColorBrush(Color.FromArgb(alpha, 250, 204, 21)); }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _state.StateChanged -= Refresh;
        _clockTimer.Stop();
        _clearTimer.Stop();
    }
}

public sealed class CustomerDisplayItemViewModel
{
    public CustomerDisplayItemViewModel(CustomerDisplayLine line, IReadOnlyList<string> columns)
        : this(line.Title, line.Barcode, line.Quantity, line.Unit, line.LineTotal, columns)
    {
        ImagePath = ResolveImagePath(line.ImageUrl);
    }

    public CustomerDisplayItemViewModel(
        string title, string? barcode, double quantity, string unit, double lineTotal,
        IReadOnlyList<string> columns)
    {
        Title = title;
        Barcode = barcode?.Trim() ?? "";
        QuantityText = $"×{quantity.ToString("0.###", CultureInfo.InvariantCulture)} {unit}";
        UnitPriceText = Tr.T($"{(quantity == 0 ? 0 : lineTotal / quantity):0.00} сом", $"{(quantity == 0 ? 0 : lineTotal / quantity):0.00} сом", $"{(quantity == 0 ? 0 : lineTotal / quantity):0.00} som", $"{(quantity == 0 ? 0 : lineTotal / quantity):0.00} som", $"{(quantity == 0 ? 0 : lineTotal / quantity):0.00} so'm");
        TotalText = Tr.T($"{lineTotal:0.00} сом", $"{lineTotal:0.00} сом", $"{lineTotal:0.00} som", $"{lineTotal:0.00} som", $"{lineTotal:0.00} so'm");
        QuantityValueText = quantity.ToString("0.###", CultureInfo.InvariantCulture);
        UnitText = unit;
        UnitPriceNumberText = CustomerDisplayViewModel.Money(quantity == 0 ? 0 : lineTotal / quantity);
        TotalNumberText = CustomerDisplayViewModel.Money(lineTotal);
        QuantityTimesPriceText = $"{QuantityValueText} {unit} × {UnitPriceNumberText}";
        foreach (var column in columns)
        {
            var value = column switch
            {
                "Product" => Title, "Quantity" => QuantityText, "Price" => UnitPriceText,
                "Discount" => "—", "Total" => TotalText, "Barcode" => Barcode, _ => "",
            };
            TableCells.Add(new CustomerDisplayTableCellViewModel(value, ColumnWidth(column)));
        }
    }

    public string Title { get; }
    public string Barcode { get; }
    public bool HasBarcode => !string.IsNullOrWhiteSpace(Barcode);
    public string BarcodeDisplay => Tr.T($"ШК: {Barcode}", $"Штрихкод: {Barcode}", $"Barcode: {Barcode}", $"Barkod: {Barcode}", $"Shtrix-kod: {Barcode}");
    public string QuantityText { get; }
    public string UnitPriceText { get; }
    public string TotalText { get; }
    public ObservableCollection<CustomerDisplayTableCellViewModel> TableCells { get; } = [];
    private static double ColumnWidth(string key) => key == "Product" ? 280 : key == "Barcode" ? 180 : 145;

    // 2026-09-28: для видов экрана покупателя («Табличная», «Карточки», «Профи», «1С»).
    /// <summary>Номер позиции в чеке (первая пробитая — 1; сверху — последняя).</summary>
    public int Number { get; init; }
    public string QuantityValueText { get; }
    public string UnitText { get; }
    public string UnitPriceNumberText { get; }
    public string TotalNumberText { get; }
    /// <summary>«2 шт × 85.00».</summary>
    public string QuantityTimesPriceText { get; }
    /// <summary>Фото из кэша миниатюр каталога (или null — тогда кружок с буквами названия).</summary>
    public string? ImagePath { get; }
    /// <summary>Настройка «Показывать фото товаров» (редактор экрана покупателя).</summary>
    public bool ShowPhoto { get; init; } = true;
    public bool HasPhoto => ShowPhoto && !string.IsNullOrWhiteSpace(ImagePath);
    public bool HasNoPhoto => !HasPhoto;

    private static string? ResolveImagePath(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;
        try
        {
            if (!imageUrl.Contains("://", StringComparison.Ordinal) && File.Exists(imageUrl))
                return imageUrl;
        }
        catch (Exception)
        {
            // путь с недопустимыми символами — значит, это не файл
        }
        return ProductThumbService.TryGetCachedPath(imageUrl);
    }
}

public sealed record CustomerDisplayTableCellViewModel(string Text, double Width);
