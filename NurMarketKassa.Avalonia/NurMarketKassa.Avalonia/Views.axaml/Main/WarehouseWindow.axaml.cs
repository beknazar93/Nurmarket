using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class WarehouseWindow : Window
{
    private readonly IBarcodeInputService _barcodeInputService;
    private readonly WarehouseViewModel _viewModel;

    public string? InitialBarcode { get; set; }

    /// <summary>Установить ДО Show() — прячет вкладки Товары/Ревизия/Списание и сразу открывает
    /// Аналитику. Используется при переходе сюда из бургер-меню Финансов, где нужен только
    /// обзор склада, а не операции над товарами.</summary>
    public bool AnalyticsOnly { get; set; }

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public WarehouseWindow() : this(
        ResolveService<WarehouseViewModel>(),
        ResolveService<IBarcodeInputService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public WarehouseWindow(WarehouseViewModel viewModel, IBarcodeInputService barcodeInputService)
    {
        _viewModel = viewModel;
        _barcodeInputService = barcodeInputService;
        InitializeComponent();
        this.FitToScreen();
        DataContext = _viewModel;
        _viewModel.RevisionLineAdded += OnRevisionLineAdded;
        InitializeWriteOffReasonPicker();
        WarehouseTabs.SelectionChanged += WarehouseTabs_SelectionChanged;
        RefreshPaidFeatureVisibility();
    }

    /// <summary>Аналитика склада и массовая печать ценников — платные доп. услуги (2026-09-04,
    /// см. LicenseKeys/UserPreferences.WarehouseAnalyticsUnlocked/PriceTagEditorUnlocked). По
    /// просьбе пользователя они не просто заблокированы поверх — их вообще не видно в складе,
    /// пока не куплены и не активированы (сейчас — только через карточки в Маркетплейс → Доп.
    /// функции; см. MarketplaceView.BuildWarehouseAnalyticsCard/BuildPriceTagEditorCard). Вызов
    /// здесь нужен на случай, если пользователь купил доп. услугу, не закрывая окно склада —
    /// маловероятно (Маркетплейс в другом окне), но раз уж эта функция уже есть.</summary>
    private void RefreshPaidFeatureVisibility()
    {
        AnalyticsTabItem.IsVisible = UserPreferences.Instance.WarehouseAnalyticsUnlocked;
        BulkPriceTagButton.IsVisible = UserPreferences.Instance.BulkPriceTagUnlocked;
    }

    /// <summary>Аналитика считается только когда кассир реально открыл эту вкладку — сразу
    /// с актуальными данными (в т.ч. после добавления/списания товара), без лишней работы
    /// на каждое открытие окна склада.</summary>
    private void WarehouseTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (WarehouseTabs.SelectedIndex == 3)
            RefreshAnalytics();
    }

    /// <summary>Списки причин списания через "Брак" собираются вручную вместо ComboBox — тот же
    /// приём, что уже надёжно решил похожую проблему с кнопками в клиентской базе.</summary>
    private void InitializeWriteOffReasonPicker()
    {
        WriteOffReasonListPanel.Children.Clear();
        foreach (var reason in _viewModel.WriteOffReasonOptions)
        {
            var button = new Button
            {
                Classes = { "SecondaryButton" },
                Content = reason,
                Tag = reason,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            button.Click += WriteOffReasonItem_Click;
            WriteOffReasonListPanel.Children.Add(button);
        }

        SetWriteOffReason(_viewModel.WriteOffReason);
    }

    private void SetWriteOffReason(string reason)
    {
        _viewModel.WriteOffReason = reason;
        WriteOffReasonButtonText.Text = reason;
    }

    private void WriteOffReasonButton_Click(object? sender, RoutedEventArgs e)
    {
        WriteOffReasonListBorder.IsVisible = !WriteOffReasonListBorder.IsVisible;
    }

    private void WriteOffReasonItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string reason })
            SetWriteOffReason(reason);
        WriteOffReasonListBorder.IsVisible = false;
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        // AnalyticsOnly просят и уже существующий ярлык из "Финансов" (FinanceWindow.
        // NavigateToStock_Click), и он не знает про платную блокировку — если аналитика не
        // куплена, её вкладка теперь вообще скрыта (см. RefreshPaidFeatureVisibility), и слепо
        // прятать ещё и Товары/Ревизию/Списание оставило бы окно полностью пустым. Поэтому
        // AnalyticsOnly действует, только когда аналитика реально разблокирована.
        if (AnalyticsOnly && UserPreferences.Instance.WarehouseAnalyticsUnlocked)
        {
            ProductsTabItem.IsVisible = false;
            RevisionTabItem.IsVisible = false;
            WriteOffTabItem.IsVisible = false;
            // Подсказка про сканер штрих-кода бессмысленна здесь — в этом режиме нет поиска
            // и редактирования товаров, только просмотр аналитики.
            ScanHintBorder.IsVisible = false;
        }

        _barcodeInputService.BarcodeScanned += OnBarcodeScanned;
        try
        {
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(InitialBarcode))
                _viewModel.HandleBarcodeScan(InitialBarcode.Trim(), isRevisionTab: true);
        }
        catch (Exception ex)
        {
            PosMessageBox.Show(this, $"Не удалось загрузить каталог: {ex.Message}", "Склад",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (AnalyticsOnly && UserPreferences.Instance.WarehouseAnalyticsUnlocked)
            WarehouseTabs.SelectedItem = AnalyticsTabItem;
    }

    public void FocusWithBarcode(string? barcode)
    {
        if (!string.IsNullOrWhiteSpace(barcode))
            _viewModel.HandleBarcodeScan(barcode.Trim(), isRevisionTab: true);
        Activate();
        Focus();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _barcodeInputService.BarcodeScanned -= OnBarcodeScanned;
        _viewModel.RevisionLineAdded -= OnRevisionLineAdded;
    }

    /// <summary>Сканер штрих-кодов — это просто клавиатура: если фокус завис в поле поиска
    /// (кассир кликнул туда, а потом сразу сканирует, не кликнув мимо), символы штрих-кода
    /// печатаются как текст в это поле вместо перехвата сканером — Window_KeyDown ниже
    /// специально пропускает обработку, пока фокус в TextBox. Esc — быстрый, заметный способ
    /// вернуть фокус на окно самому, не переключая вкладки туда-обратно.</summary>
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        if (sender is TextBox tb)
            tb.Text = "";
        Focus();
    }

    /// <summary>Фильтр "Единица продажи" (2026-09-07) — четыре радиокнопки над таблицей Склада,
    /// см. WarehouseViewModel.SetSaleUnitFilter.</summary>
    private void SaleUnitFilter_Changed(object? sender, RoutedEventArgs e)
    {
        var filter = sender switch
        {
            _ when ReferenceEquals(sender, SaleUnitWeightRadio) => "weight",
            _ when ReferenceEquals(sender, SaleUnitPieceRadio) => "piece",
            _ when ReferenceEquals(sender, SaleUnitPiecePackageRadio) => "piecepackage",
            _ => "all",
        };
        _viewModel.SetSaleUnitFilter(filter);
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        // Same guard MainWindow's scanner handler uses: without it, every keystroke typed
        // into a search/quantity box here (e.g. the Revision/Write-off name search added
        // alongside barcode scanning) also feeds the barcode buffer, which can intercept
        // and garble normal typing instead of leaving it to the focused TextBox.
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        _barcodeInputService.ProcessKeyDown(e);
    }

    private void OnBarcodeScanned(string barcode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Tab order: 0 = Товары, 1 = Ревизия, 2 = Списание. This used to check index 0,
            // which was the Revision tab back when the Товары list didn't exist yet — after it
            // was added as the new first tab, scanning while browsing Товары was silently
            // routed into HandleBarcodeScan(isRevisionTab: true), injecting a stray revision line.
            var selectedIndex = WarehouseTabs.SelectedIndex;
            if (selectedIndex == 0)
                return;

            var isRevisionTab = selectedIndex == 1;
            _viewModel.HandleBarcodeScan(barcode, isRevisionTab);
            if (!isRevisionTab)
                WriteOffQuantityBox?.Focus();
        });
    }

    private async void PrintLabel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        var dialog = new LabelPrintDialog(product);
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private async void PrintPriceTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;
        if (!EnsurePriceTagEditorUnlocked())
            return;

        var dialog = new PriceTagPrintDialog(product);
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private void BulkImport_Click(object? sender, RoutedEventArgs e) => BulkImportWindow.Open(this);

    private async void BulkPriceTag_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureBulkPriceTagUnlocked())
            return;

        await BulkPriceTagPrintDialog.Open(this).ConfigureAwait(true);
    }

    private async void OpenLabelEditorTab_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureLabelEditorUnlocked())
            return;

        var editor = new LabelTemplateEditorDialog();
        await editor.ShowDialog(this).ConfigureAwait(true);
    }

    /// <summary>Редактор этикеток — стал платной доп. услугой (2026-09-07, раньше был бесплатным).
    /// Тот же паттерн, что и EnsurePriceTagEditorUnlocked ниже.</summary>
    private bool EnsureLabelEditorUnlocked()
    {
        if (UserPreferences.Instance.LabelEditorUnlocked)
            return true;

        var serial = SerialActivationDialog.Show(this, Tr.T("Редактор этикетки", "Этикетка редактору", "Label editor", "Etiket düzenleyici", "Yorliq muharriri"));
        if (serial == null)
            return false;

        var isPermanent = LicenseKeys.IsPermanentSerial("labeleditor", serial);
        if (!isPermanent && !LicenseKeys.IsMasterSerial(serial))
        {
            PosAlertDialog.Show(this,
                Tr.T("Неверный серийный номер", "Серия номери туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return false;
        }

        UserPreferences.Instance.LabelEditorUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("labeleditor");

        PosAlertDialog.Show(this,
            Tr.T("Редактор этикетки активирован", "Этикетка редактору иштетилди", "Label editor activated", "Etiket düzenleyici etkinleştirildi", "Yorliq muharriri faollashtirildi"),
            Tr.T(
                "Теперь доступен редактор этикеток.",
                "Эми этикетка редактору жеткиликтүү."),
            PosAlertKind.Success);
        return true;
    }

    /// <summary>Редактор ценников — тоже платная доп. услуга (2026-09-04). Если ещё не
    /// разблокирован, сразу предлагает ввести код (без промежуточного "это платно" алерта —
    /// один клик до ввода серийника, отменить можно прямо в этом же диалоге).</summary>
    private bool EnsurePriceTagEditorUnlocked()
    {
        if (UserPreferences.Instance.PriceTagEditorUnlocked)
            return true;

        var serial = SerialActivationDialog.Show(this, Tr.T("Редактор ценников", "Ценник редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorliqlari muharriri"));
        if (serial == null)
            return false;

        var isPermanent = LicenseKeys.IsPermanentSerial("pricetag", serial);
        if (!isPermanent && !LicenseKeys.IsMasterSerial(serial))
        {
            PosAlertDialog.Show(this,
                Tr.T("Неверный серийный номер", "Серия номери туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return false;
        }

        UserPreferences.Instance.PriceTagEditorUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("pricetag");

        PosAlertDialog.Show(this,
            Tr.T("Редактор ценников активирован", "Ценник редактору иштетилди", "Price tag editor activated", "Fiyat etiketi düzenleyici etkinleştirildi", "Narx yorliqlari muharriri faollashtirildi"),
            Tr.T(
                "Теперь доступна печать ценника для одного товара: Склад → Товары → кнопка «💲 Ценник» на карточке товара.",
                "Эми бир товар үчүн ценник басып чыгаруу жеткиликтүү: Склад → Товарлар → товар карточкасындагы «💲 Ценник» баскычы."),
            PosAlertKind.Success);
        return true;
    }

    /// <summary>Массовая печать ценников — отдельная от EnsurePriceTagEditorUnlocked доп. услуга
    /// (2026-09-04, по явной просьбе пользователя: активация одной не должна включать другую),
    /// хотя серийник и диалог активации те же самые (единый мастер-ключ для всех доп. услуг).</summary>
    private bool EnsureBulkPriceTagUnlocked()
    {
        if (UserPreferences.Instance.BulkPriceTagUnlocked)
            return true;

        var serial = SerialActivationDialog.Show(this, Tr.T("Массовая печать ценников", "Ценниктерди массалык басып чыгаруу", "Bulk price tag printing", "Toplu fiyat etiketi baskısı", "Ommaviy narx yorliqlarini chop etish"));
        if (serial == null)
            return false;

        var isPermanent = LicenseKeys.IsPermanentSerial("bulktag", serial);
        if (!isPermanent && !LicenseKeys.IsMasterSerial(serial))
        {
            PosAlertDialog.Show(this,
                Tr.T("Неверный серийный номер", "Серия номери туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return false;
        }

        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("bulktag");
        RefreshPaidFeatureVisibility();

        PosAlertDialog.Show(this,
            Tr.T("Массовая печать ценников активирована", "Ценниктерди массалык басып чыгаруу иштетилди", "Bulk price tag printing activated", "Toplu fiyat etiketi baskısı etkinleştirildi", "Ommaviy narx yorlig'i chop etish faollashtirildi"),
            Tr.T(
                "Теперь доступна кнопка «🏷 Массовая печать ценников» в Склад → Товары — выберите нужные товары и распечатайте ценники сразу для всех.",
                "Эми Склад → Товарлар бетинде «🏷 Ценниктерди массалык басып чыгаруу» баскычы жеткиликтүү — керектүү товарларды тандап, баарына бирден ценник басып чыгарыңыз."),
            PosAlertKind.Success);
        return true;
    }

    private async void AddProduct_Click(object? sender, RoutedEventArgs e)
    {
        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null)
        {
            prompts?.ShowToast("Добавление товара недоступно в этом режиме.", isWarning: true);
            return;
        }

        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ProductEditDialog(catalogApi, null);
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (saved)
        {
            prompts?.ShowToast("Товар сохранён.");
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
    }

    private async void EditProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null)
        {
            prompts?.ShowToast("Редактирование товара недоступно в этом режиме.", isWarning: true);
            return;
        }

        // Свежая карточка с сервера (2026-09-07): плитка из SQLite-кэша не хранит скидку, наценку,
        // описание, оптовую цену, страну, вес и доп. штрихкоды (LocalProductRecord их не имеет) —
        // форма показывала их пустыми, а «Сохранить» отправлял пустые значения и СТИРАЛ их на
        // сервере. Офлайн или ошибка — открываем как раньше, из кэша.
        var toEdit = product;
        try
        {
            var detail = await catalogApi.ProductsDetailAsync(product.Id).ConfigureAwait(true);
            var apiBaseUrl = App.AppHost?.Services.GetService<AppSettings>()?.ApiBaseUrl ?? "";
            if (detail is { } json && ProductCatalogMapper.TryTile(json, apiBaseUrl) is { } fresh)
            {
                fresh.ProductImagePath = product.ProductImagePath;
                toEdit = fresh;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Product detail fetch before edit failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }

        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ProductEditDialog(catalogApi, toEdit);
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (saved)
        {
            prompts?.ShowToast("Товар сохранён.");
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Кнопка "Удалить" в строке таблицы товаров (2026-09-07). Необратимо — поэтому
    /// сначала PosConfirmDialog с красной кнопкой; после успешного DELETE каталог перечитывается
    /// с сервера тем же путём, что и после редактирования (RefreshFromApiAsync → sync выкинет
    /// удалённый товар из локального кэша и из списка).</summary>
    private async void DeleteProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        var permissions = App.AppHost?.Services.GetService<IPermissionService>();
        if (catalogApi is null)
        {
            prompts?.ShowToast("Удаление товара недоступно в этом режиме.", isWarning: true);
            return;
        }

        var confirmed = PosConfirmDialog.Show(
            this,
            Tr.T("Удалить товар", "Товарды өчүрүү", "Delete product", "Ürünü sil", "Mahsulotni o'chirish"),
            Tr.T($"Удалить «{product.Title}»? Товар исчезнет из каталога NurCRM и из кассы. Это действие нельзя отменить.",
                 $"«{product.Title}» өчүрүлсүнбү? Товар NurCRM каталогунан жана кассадан жоголот. Бул аракетти артка кайтарууга болбойт."),
            Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
            Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            PosConfirmAccent.Danger);
        if (!confirmed)
            return;

        // 2026-09-08: личный код доступа сотрудника (владелец/админ проходит без кода) —
        // см. EmployeeAccessGate. Не активен, пока в Настройки → Сотрудники не задан хотя бы
        // один код для удаления со склада.
        if (EmployeeAccessGate.IsActiveFor(EmployeeAccessGate.WarehouseDelete, permissions) &&
            prompts != null &&
            !await prompts.ConfirmWithCodeAsync(
                Tr.T("Удаление товара со склада", "Товарды кампадан өчүрүү", "Deleting product from warehouse", "Depodan ürün silme", "Mahsulotni ombordan o'chirish"),
                Tr.T($"Введите свой код доступа, чтобы удалить «{product.Title}» со склада.",
                    $"«{product.Title}» товарын кампадан өчүрүү үчүн жеке кодуңузду киргизиңиз.",
                    $"Enter your access code to delete \"{product.Title}\" from the warehouse.",
                    $"\"{product.Title}\" ürününü depodan silmek için erişim kodunuzu girin.",
                    $"\"{product.Title}\" mahsulotini ombordan o'chirish uchun kirish kodingizni kiriting."),
                entered => EmployeeAccessGate.TryValidate(EmployeeAccessGate.WarehouseDelete, entered)).ConfigureAwait(true))
            return;

        try
        {
            // 2026-09-09: офлайн (обычный или автономный режим) — удаляем прямо из локальной
            // SQLite, минуя ICatalogApiService.DeleteProductAsync (реальный HTTP к NurCRM).
            if (OfflineModeHelper.UseLocalOperations)
            {
                LocalProductEditor.DeleteLocally(product.Id);
                prompts?.ShowToast("Товар удалён.");
                await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
                return;
            }

            var deleted = await catalogApi.DeleteProductAsync(product.Id).ConfigureAwait(true);
            if (!deleted)
            {
                prompts?.ShowToast("Не удалось удалить товар: сервер не принял запрос на удаление.", isWarning: true);
                return;
            }

            prompts?.ShowToast("Товар удалён.");
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            prompts?.ShowToast($"Не удалось удалить товар: {ex.Message}", isWarning: true);
        }
    }

    private async void UploadPhoto_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var photoPicker = App.AppHost?.Services.GetService<ISettingsImagePicker>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null || photoPicker is null)
        {
            prompts?.ShowToast("Загрузка фото недоступна в этом режиме.", isWarning: true);
            return;
        }

        var filePath = await photoPicker.PickProductPhotoAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var uploadedUrl = await catalogApi
                .UploadProductImageAsync(product.Id, filePath, isPrimary: true)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(uploadedUrl))
            {
                prompts?.ShowToast("Не удалось загрузить фото.", isWarning: true);
                return;
            }

            // Show the picked file immediately — same bytes just landed on the server,
            // no need to round-trip a redownload through the thumbnail cache.
            product.ProductImagePath = filePath;
            App.AppHost?.Services.GetService<MySqlAuditService>()?
                .LogEvent("catalog", "product_photo_upload", new { productId = product.Id });
            prompts?.ShowToast("Фото товара загружено.");
        }
        catch (ApiException ex)
        {
            prompts?.ShowToast($"Загрузка фото: {ex.Message}", isWarning: true);
        }
        catch (HttpRequestException)
        {
            prompts?.ShowToast("Не удалось загрузить фото — нет сети.", isWarning: true);
        }
    }

    private void OnRevisionLineAdded(RevisionLineVm line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnRevisionLineAdded(line));
            return;
        }

        RevisionGrid.SelectedItem = line;
        RevisionGrid.ScrollIntoView(line, null);
    }

    #region Аналитика склада/товаров

    private sealed class KpiCardVm
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
    }

    private sealed class AnalyticsTopValueRow
    {
        public string Title { get; init; } = "";
        public string StockText { get; init; } = "";
        public string PriceText { get; init; } = "";
        public string ValueText { get; init; } = "";
    }

    private sealed class AnalyticsCategoryRow
    {
        public string CategoryName { get; init; } = "";
        public string SkuCountText { get; init; } = "";
        public string StockText { get; init; } = "";
        public string ValueText { get; init; } = "";
    }

    private static double ParsePriceValue(string? priceLine) =>
        LocalCartService.ParsePrice(priceLine ?? "");

    /// <summary>Считается локально из уже загруженного каталога (CatalogCacheService.Products) —
    /// без отдельных запросов к серверу, поэтому мгновенно и без дополнительной нагрузки.</summary>
    private void RefreshAnalytics()
    {
        var products = CatalogCacheService.Products.ToList();

        var skuCount = products.Count;
        var outOfStock = products.Count(p => p.Quantity <= 0);
        var lowStock = products.Count(p => p.IsLowStock && p.Quantity > 0);
        var purchaseValue = products.Sum(p => p.Quantity * p.PurchasePrice);
        var saleValue = products.Sum(p => p.Quantity * ParsePriceValue(p.PriceLine));

        AnalyticsKpiPanel.ItemsSource = new List<KpiCardVm>
        {
            new() { Label = Tr.T("Всего товаров (SKU)", "Бардык товарлар (SKU)", "Total products (SKU)", "Toplam ürün (SKU)", "Jami mahsulotlar (SKU)"), Value = skuCount.ToString() },
            new() { Label = Tr.T("Нет в наличии", "Дүкөндө жок", "Out of stock", "Stokta yok", "Mavjud emas"), Value = outOfStock.ToString() },
            new() { Label = Tr.T("Низкий остаток", "Аз калды", "Low stock", "Düşük stok", "Kam qoldiq"), Value = lowStock.ToString() },
            new() { Label = Tr.T("Остаток по закупке", "Сатып алуу баасы боюнча калдык", "Stock at purchase price", "Alış fiyatına göre stok", "Sotib olish narxi bo'yicha qoldiq"), Value = $"{purchaseValue:N0} сом" },
            new() { Label = Tr.T("Остаток по продаже", "Сатуу баасы боюнча калдык", "Stock at sale price", "Satış fiyatına göre stok", "Sotuv narxi bo'yicha qoldiq"), Value = $"{saleValue:N0} сом" },
        };

        var topValue = products
            .Select(p => new { Product = p, Value = p.Quantity * ParsePriceValue(p.PriceLine) })
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .Take(10)
            .ToList();

        AnalyticsTopValueGrid.ItemsSource = topValue
            .Select(x => new AnalyticsTopValueRow
            {
                Title = x.Product.Title,
                StockText = x.Product.Quantity.ToString("0.###"),
                PriceText = $"{ParsePriceValue(x.Product.PriceLine):N2} сом",
                ValueText = $"{x.Value:N2} сом",
            })
            .ToList();
        BarChartRenderer.Render(AnalyticsTopValueChart, topValue
            .Select(x => (x.Product.Title, (double)x.Value, $"{x.Value:N0} сом"))
            .ToList());

        var byCategory = products
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz") : p.Category!.Trim())
            .Select(g => new
            {
                Name = g.Key,
                SkuCount = g.Count(),
                Stock = g.Sum(p => p.Quantity),
                Value = g.Sum(p => p.Quantity * ParsePriceValue(p.PriceLine)),
            })
            .OrderByDescending(g => g.Value)
            .ToList();

        AnalyticsCategoryGrid.ItemsSource = byCategory
            .Select(g => new AnalyticsCategoryRow
            {
                CategoryName = g.Name,
                SkuCountText = g.SkuCount.ToString(),
                StockText = g.Stock.ToString("0.###"),
                ValueText = $"{g.Value:N2} сом",
            })
            .ToList();
        var categoryChartData = byCategory
            .Take(10)
            .Select(g => (g.Name, g.Value, $"{g.Value:N0} сом"))
            .ToList();
        BarChartRenderer.Render(AnalyticsCategoryChart, categoryChartData);
        BarChartRenderer.RenderPie(AnalyticsCategoryPie, categoryChartData);
    }

    #endregion

    #region Window Controls & Dragging

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void ExpandWriteOffHistory_Click(object? sender, RoutedEventArgs e) =>
        WriteOffHistoryWindow.Open(this);

    /// <summary>
    /// Позволяет перетаскивать кастомное окно за шапку или пустые области.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    #endregion
}