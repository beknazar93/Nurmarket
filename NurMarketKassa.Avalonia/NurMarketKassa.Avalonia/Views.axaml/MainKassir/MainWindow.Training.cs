using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.PrinterSetup;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>2026-11-13 (Н-1 и Н-2, решение владельца): «первый час» новичка в кассе —
/// мастер первого запуска (FirstRunWizardWindow), учебный режим (TrainingMode: жёлтая полоса под шапкой, подсказка шага, «Выйти»), шпаргалка клавиш (KassaHotkeysWindow)
/// и количество «3 *» перед сканом без курсора в поле (TypedQuantityTracker).</summary>
public partial class MainWindow
{
    // ───────────── шпаргалка клавиш (Н-2) ─────────────

    private bool _cheatSheetOpen;

    internal async void OpenHotkeysCheatSheet()
    {
        if (_cheatSheetOpen)
            return;
        _cheatSheetOpen = true;
        try
        {
            _viewModel.CloseSideMenu();
            await new KassaHotkeysWindow(_hotkeys).ShowDialog(PosDialogHost.ResolveOwner(this)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Шпаргалка клавиш не открылась: {ex.Message}", "WARNING");
        }
        finally
        {
            _cheatSheetOpen = false;
            RestoreScannerFocus();
        }
    }

    private void OpenHotkeysCheatSheet_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenHotkeysCheatSheet();

    /// <summary>«?» и F1 (если на F1 нет группы быстрых товаров) — шпаргалка. Вызывается из OnMainWindowKeyDown, когда фокус не в поле ввода и сканер не набирает код.</summary>
    private bool TryOpenCheatSheetByKey(KeyEventArgs e)
    {
        var f1 = e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None && !_viewModel.Catalog.HasProductsInHotkeyGroup("F1");
        if (!f1 && !(e.Key == Key.OemQuestion && e.KeyModifiers == KeyModifiers.Shift)) // «?» — Shift+/ (в русской раскладке — F1 или меню «⋯»)
            return false;
        if (!f1 && _barcodeInputService.HasBufferedInput)
            return false; // «?» внутри кода сканера (Code128) — часть кода
        e.Handled = true;
        OpenHotkeysCheatSheet();
        return true;
    }

    // ───────────── «3 *» и скан без курсора в поле (Н-2) ─────────────

    private readonly TypedQuantityTracker _typedQuantity = new();

    /// <summary>Цифры и «*», набранные вне полей ввода: «3», «*» — количество для следующего скана. Цифры не глушим (их по-прежнему получает буфер сканера).
    /// true — «*» принята как количество.</summary>
    private bool TrackTypedQuantity(KeyEventArgs e)
    {
        var now = Environment.TickCount64;
        if (e.KeyModifiers == KeyModifiers.None && e.Key is >= Key.D0 and <= Key.D9)
        {
            _typedQuantity.Digit((char)('0' + (e.Key - Key.D0)), now);
            return false;
        }
        if (e.KeyModifiers == KeyModifiers.None && e.Key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            _typedQuantity.Digit((char)('0' + (e.Key - Key.NumPad0)), now);
            return false;
        }
        var star = (e.Key == Key.Multiply && e.KeyModifiers == KeyModifiers.None) || (e.Key == Key.D8 && e.KeyModifiers == KeyModifiers.Shift);
        if (!star)
        {
            if (e.Key is not (Key.LeftShift or Key.RightShift))
                _typedQuantity.Reset();
            return false;
        }
        if (_typedQuantity.Star(now) is not { } quantity || !KassaMainViewRules.SingleInputRowEnabled() || OperatingSystem.IsAndroid())
            return false;
        e.Handled = true;
        (_barcodeInputService as AvaloniaKeyboardWedgeBarcodeService)?.DiscardBuffer();
        _viewModel.Basket.EnqueueBarcode(quantity + "*");
        return true;
    }

    // ───────────── учебный режим (Н-1) ─────────────

    private bool _trainingHooked;

    private void HookTrainingMode()
    {
        if (_trainingHooked)
            return;
        _trainingHooked = true;
        TrainingMode.Changed += OnTrainingModeChanged;
        // Пункты меню «Помощь» (в тестах окно создаётся без InitializeApplicationAsync — поэтому и здесь).
        _viewModel.SideMenu.NavigateFirstRunWizardAction ??= NavigateFirstRunWizard;
        _viewModel.SideMenu.NavigateTrainingSaleAction ??= NavigateTrainingSale;
        _viewModel.Basket.Lines.CollectionChanged += (_, _) =>
        {
            if (TrainingMode.IsActive)
                Dispatcher.UIThread.Post(ApplyTrainingBar);
        };
        Tr.LanguageChanged += OnTrainingModeChanged;
        Closed += (_, _) =>
        {
            TrainingMode.Changed -= OnTrainingModeChanged;
            Tr.LanguageChanged -= OnTrainingModeChanged;
            TrainingMode.Exit(); // окно кассы закрыто (выход, смена пользователя) — учебный режим не переживает его
        };
        ApplyTrainingBar();
    }

    private void OnTrainingModeChanged() => Dispatcher.UIThread.Post(ApplyTrainingBar);

    /// <summary>Жёлтая полоса под шапкой: «Учебный режим — деньги не учитываются», подсказка текущего шага и «Выйти».</summary>
    private void ApplyTrainingBar()
    {
        var active = TrainingMode.IsActive;
        TrainingBar.IsVisible = active;
        if (!active)
            return;
        TrainingBarTitle.Text = TrainingTitle;
        TrainingBarHint.Text = TrainingHint(_viewModel.Basket.Lines.Count > 0, TrainingMode.SalesDone);
        TrainingExitText.Text = Tr.T("Выйти из учебного режима", "Окуу режиминен чыгуу", "Leave training mode", "Eğitim modundan çık", "O'quv rejimidan chiqish");
    }

    internal static string TrainingTitle => Tr.T("Учебный режим — деньги не учитываются", "Окуу режими — акча эсепке алынбайт", "Training mode — money is not recorded",
        "Eğitim modu — para kaydedilmez", "O'quv rejimi — pul hisobga olinmaydi");

    /// <summary>Подсказка шага учебной продажи (чистая функция — проверяется тестом).</summary>
    internal static string TrainingHint(bool hasItems, int salesDone)
    {
        if (hasItems)
            return Tr.T("Шаг 2 из 3. Товар в чеке. Нажмите большую кнопку «Оплатить» (или Enter).", "3 кадамдын 2-кадамы. Товар чекте. Чоң «Төлөө» баскычын (же Enter) басыңыз.",
                "Step 2 of 3. The item is in the receipt. Press the big “Pay” button (or Enter).", "Adım 2 / 3. Ürün fişte. Büyük «Öde» düğmesine (veya Enter) basın.",
                "3 qadamdan 2-qadam. Mahsulot chekda. Katta «To'lash» tugmasini (yoki Enter) bosing.");
        if (salesDone > 0)
            return Tr.T("Готово — продажа прошла. Попробуйте ещё раз или выйдите из учебного режима и работайте по-настоящему.",
                "Даяр — сатуу өттү. Дагы бир жолу аракет кылыңыз же окуу режиминен чыгып, чындап иштеңиз.",
                "Done — the sale went through. Try again or leave training mode and work for real.",
                "Tamam — satış gerçekleşti. Tekrar deneyin veya eğitim modundan çıkıp gerçek çalışın.",
                "Tayyor — sotuv o'tdi. Yana urinib ko'ring yoki o'quv rejimidan chiqib, haqiqiy ishlang.");
        return Tr.T("Шаг 1 из 3. Отсканируйте товар или нажмите на него в каталоге слева.", "3 кадамдын 1-кадамы. Товарды сканерлеңиз же сол жактагы каталогдон басыңыз.",
            "Step 1 of 3. Scan a product or press it in the catalog on the left.", "Adım 1 / 3. Bir ürün okutun veya soldaki katalogda üzerine basın.",
            "3 qadamdan 1-qadam. Mahsulotni skanerlang yoki chapdagi katalogda ustiga bosing.");
    }

    /// <summary>Включить учебный режим. Только с одним пустым чеком: настоящие товары учебная оплата стёрла бы.</summary>
    internal bool EnterTrainingMode()
    {
        HookTrainingMode();
        if (TrainingMode.IsActive)
            return true;
        if (!_viewModel.Basket.CanEnterTraining)
        {
            _prompts.ShowWarning(Tr.T(
                "Сначала закончите или уберите открытые чеки: учебная продажа начинается с одного пустого чека.",
                "Адегенде ачык чектерди бүтүрүңүз же алып салыңыз: окуу сатуусу бир бош чектен башталат.",
                "Finish or remove the open receipts first: a training sale starts with one empty receipt.",
                "Önce açık fişleri bitirin veya kaldırın: eğitim satışı tek boş fişle başlar.",
                "Avval ochiq cheklarni tugating yoki olib tashlang: o'quv sotuvi bitta bo'sh chekdan boshlanadi."));
            return false;
        }
        _viewModel.CloseSideMenu();
        SaveApplicationState(); // состояние до учебного режима — на диск; дальше, пока режим включён, оно не пишется (см. ScheduleApplicationStateSave)
        TrainingMode.Enter();
        ApplyTrainingBar();
        RestoreScannerFocus();
        return true;
    }

    internal void ExitTrainingMode()
    {
        if (!TrainingMode.IsActive)
            return;
        _viewModel.Basket.ClearTrainingReceipt();
        _viewModel.Basket.ResetPendingQuantity();
        TrainingMode.Exit();
        ApplyTrainingBar();
        _viewModel.Basket.CartMessage = Tr.T("Учебный режим выключен. Теперь продажи настоящие.", "Окуу режими өчүрүлдү. Эми сатуулар чыныгы.",
            "Training mode is off. Sales are real now.", "Eğitim modu kapalı. Artık satışlar gerçek.", "O'quv rejimi o'chirildi. Endi sotuvlar haqiqiy.");
        RestoreScannerFocus();
    }

    private void TrainingExit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ExitTrainingMode();

    internal void NavigateTrainingSale() => EnterTrainingMode();

    // ───────────── мастер первого запуска (Н-1) ─────────────

    private bool _firstRunWizardOpen;

    /// <summary>Сам при запуске — только новому магазину: мастер ещё не показывали, каталог пуст, есть право «Настройки». Ждёт вопрос о виде магазина и первую загрузку каталога.</summary>
    private async Task MaybeOfferFirstRunWizardAsync()
    {
        try
        {
            if (FirstRunWizardWindow.IsDone())
                return;
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true); // каталог после входа ещё догружается
            for (var i = 0; i < 40 && (_viewModel.Catalog.IsLoading || OwnedWindows.Count > 0); i++)
                await Task.Delay(500).ConfigureAwait(true);
            if (!IsVisible || _windowCts.IsCancellationRequested)
                return;
            var count = SafeProductCount();
            if (!FirstRunWizardWindow.ShouldOfferAtStartup(FirstRunWizardWindow.IsDone(), count, _permissions.HasPermission(PosPermissions.ViewSettings)))
            {
                if (count > 0)
                    FirstRunWizardWindow.MarkDone(); // магазин уже с товарами — мастер сам не нужен (из меню «Помощь» доступен)
                return;
            }
            await ShowFirstRunWizardAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Мастер первого запуска не показан: {ex.Message}", "WARNING");
        }
    }

    private static int SafeProductCount()
    {
        try
        {
            return LocalProductRepository.Instance.LoadAllTiles().Count;
        }
        catch
        {
            return 0;
        }
    }

    internal void NavigateFirstRunWizard() => _ = ShowFirstRunWizardAsync();

    internal async Task ShowFirstRunWizardAsync()
    {
        if (_firstRunWizardOpen)
            return;
        if (!Authorize(PosPermissions.ViewSettings))
            return;
        _firstRunWizardOpen = true;
        try
        {
            _viewModel.CloseSideMenu();
            FirstRunWizardWindow? wizard = null;
            var actions = new FirstRunWizardWindow.Actions
            {
                SphereText = () => MarketSpheres.Profile.Title.Get(),
                ChooseSphere = !MarketSphereLock.CanChangeNow
                    ? null
                    : async () => await MarketSphereChoiceWindow.ShowAndApplyAsync(wizard!).ConfigureAwait(true) is null
                        ? null
                        : Tr.T("Вид магазина: ", "Дүкөндүн түрү: ", "Store type: ", "Mağaza türü: ", "Do'kon turi: ") + MarketSpheres.Profile.Title.Get(),
                SetupPrinter = async () =>
                {
                    var result = await PrinterSetupWizardWindow.ShowAsync(wizard, PrinterRole.Receipt).ConfigureAwait(true);
                    return result is null ? null : Tr.T("Принтер чеков подключён.", "Чек принтери туташтырылды.", "The receipt printer is connected.", "Fiş yazıcısı bağlandı.", "Chek printeri ulandi.");
                },
                SetupScale = async () =>
                {
                    var settings = App.GetRequiredService<NurMarketKassa.AvaloniaHost.Views.PosSettingsWindow>();
                    await settings.ShowDialog(wizard!).ConfigureAwait(true);
                    return Tr.T("Настройки весов открывались. Вернуться к ним: меню ☰ → «Настройки» → «Весы».", "Тараза жөндөөлөрү ачылды. Аларга кайтуу: ☰ меню → «Жөндөөлөр» → «Тараза».",
                        "Scale settings were opened. To return: menu ☰ → “Settings” → “Scales”.", "Terazi ayarları açıldı. Geri dönmek için: ☰ menü → «Ayarlar» → «Terazi».",
                        "Tarozi sozlamalari ochildi. Qaytish: ☰ menyu → «Sozlamalar» → «Tarozi».");
                },
                AddProducts = async () =>
                {
                    var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
                    if (catalogApi is null)
                        return null;
                    var dialog = new ProductEditDialog(catalogApi, null, quickAddMode: true);
                    if (!await dialog.ShowDialog<bool>(wizard!).ConfigureAwait(true))
                        return null;
                    try
                    {
                        await ServerAnswerWait.ForegroundAsync("catalog/refresh", t => CatalogCacheService.RefreshProductsAsync([dialog.SavedProductId], t)).ConfigureAwait(true);
                    }
                    catch (ServerNotAnsweringException)
                    {
                        _ = CatalogCacheService.RefreshProductsAsync([dialog.SavedProductId]);
                    }
                    await _viewModel.Catalog.RepublishFromLocalAsync().ConfigureAwait(true);
                    return FirstRunWizardWindow.ProductCountText(SafeProductCount());
                },
                ImportProducts = async () =>
                {
                    // 2026-11-13 (Л-10): новый мастер «Массовая загрузка товаров». «Попробовать продажу на примере» — демо-набор товаров (только в памяти,
                    // только пока включён учебный режим): мастер первого запуска закрывается и сразу начинается учебная продажа.
                    var import = await NurMarketKassa.AvaloniaHost.Views.BulkImportWizardWindow.ShowForOnboardingAsync(wizard!).ConfigureAwait(true);
                    if (import.DemoRequested)
                    {
                        wizard!.Close(true);
                        return null;
                    }
                    await _viewModel.Catalog.RepublishFromLocalAsync().ConfigureAwait(true);
                    return FirstRunWizardWindow.ProductCountText(SafeProductCount());
                },
                ProductCount = SafeProductCount,
            };
            wizard = new FirstRunWizardWindow(actions);
            var startTraining = await wizard.ShowDialog<bool>(PosDialogHost.ResolveOwner(this)).ConfigureAwait(true);
            FirstRunWizardWindow.MarkDone();
            PosLogger.Log($"[UI] Мастер первого запуска: закрыт ({(startTraining ? "учебная продажа" : "без учебной продажи")}).", "SETTINGS");
            if (startTraining)
                EnterTrainingMode();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Мастер первого запуска: ошибка: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
        finally
        {
            _firstRunWizardOpen = false;
            RestoreScannerFocus();
        }
    }
}
