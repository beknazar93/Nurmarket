using NurMarketKassa.Core.Application;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Клиентская база: просмотр, поиск и добавление клиентов (/api/main/clients/).</summary>
public partial class ClientsWindow : Window, INotifyPropertyChanged, IOwnerSection
{
    private readonly IClientsApiService _clientsApi;
    private readonly List<ClientRow> _allClients = new();

    private string _searchText = "";
    // 2026-10-19, этап О8: формы «Новый клиент» сверху больше нет — окно NewClientDialog (поля _newClient* убраны).
    private bool _isLoading;
    /// <summary>2026-10-19, этап О8: порядок списка — «new» (новые сверху), «name», «debt»; фильтр «Только должники».</summary>
    private string _sort = "new";
    private bool _onlyDebtors;
    /// <summary>Клиенты загружены хотя бы раз (до этого пустое состояние не показываем).</summary>
    private bool _clientsLoaded;
    private string _errorMessage = "";
    private CancellationTokenSource? _loadCts;

    private ClientRow? _selectedClient;
    private string _editName = "";
    private string _editPhone = "";
    private string _editEmail = "";
    private string _editAddress = "";
    private bool _isCardBusy;
    private string _cardErrorMessage = "";

    public ObservableCollection<ClientRow> FilteredClients { get; } = new();

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public ClientsWindow() : this(ResolveService<IClientsApiService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public ClientsWindow(IClientsApiService clientsApi)
    {
        _clientsApi = clientsApi;
        InitializeComponent();
        DataContext = this;
        // Esc: сначала карточка клиента, потом само окно.
        EscapeKey.Attach(this, () =>
        {
            if (SelectedClient is null)
                return false;
            SelectedClient = null;
            return true;
        });

        UpdateCardButtonStates();
        ApplyTexts();
        InitDebtButton();
        // 2026-10-21: вкладка «Документы» (КП, счета, накладные) — только у владельца, см. ClientsWindow.Docs.cs.
        InitDocs();
    }

    private void CloseCardButton_Click(object? sender, RoutedEventArgs e) => SelectedClient = null;

    /// <summary>Копирует персональную ссылку на бота для этого клиента. Перейдя по ней,
    /// покупатель разрешает боту себе писать — только после этого касса сможет прислать ему
    /// напоминание о долге. Написать по номеру телефона Telegram не позволяет никому.</summary>
    private async void TelegramInviteButton_Click(object? sender, RoutedEventArgs e)
    {
        var clientId = SelectedClient?.Id;
        if (string.IsNullOrWhiteSpace(clientId))
            return;

        TelegramInviteHint.IsVisible = true;

        var link = TelegramSubscriberStore.BuildInviteLink(clientId!);
        if (link == null)
        {
            TelegramInviteHint.Text = Tr.T(
                "Сначала подключите бота в «Настройки → Операции»: имя бота касса узнаёт при определении получателя.",
                "Адегенде ботту «Жөндөөлөр → Операциялар» бөлүмүндө туташтырыңыз: боттун атын касса алуучуну аныктаганда билет.",
                "First connect the bot in “Settings → Operations”: the till learns the bot's name when it detects the recipient.",
                "Önce botu «Ayarlar → İşlemler» bölümünden bağlayın: kasa, botun adını alıcıyı belirlerken öğrenir.",
                "Avval botni «Sozlamalar → Operatsiyalar» bo'limida ulang: kassa bot nomini qabul qiluvchini aniqlashda bilib oladi.");
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(link).ConfigureAwait(true);

        TelegramInviteHint.Text = Tr.T($"Ссылка скопирована: {link}\n", $"Шилтеме көчүрүлдү: {link}\n", $"Link copied: {link}\n", $"Bağlantı kopyalandı: {link}\n", $"Havola nusxalandi: {link}\n")
            + Tr.T("Отправьте её клиенту. После нажатия «Старт» он начнёт получать напоминания.",
                "Аны клиентке жөнөтүңүз. «Старт» баскычын баскандан кийин ал эскертмелерди ала баштайт.",
                "Send it to the client. Once they press “Start”, they'll begin receiving reminders.",
                "Bunu müşteriye gönderin. «Başlat»a bastıktan sonra hatırlatmaları almaya başlar.",
                "Uni mijozga yuboring. «Start» tugmasini bosgach, u eslatmalarni ola boshlaydi.");
    }

    /// <summary>Открывает историю покупок выбранного клиента отдельным окном. В карточке для
    /// неё места нет: нужны номер чека, способ оплаты и состав, а это таблица, а не список из
    /// двух колонок.</summary>
    private void PurchasesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client || string.IsNullOrWhiteSpace(client.Id))
            return;

        try
        {
            new ClientPurchasesWindow(client.Id, client.FullName).Show(this);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История покупок не открылась: {ex}", "WARNING");
        }
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;
        await LoadClientsAsync().ConfigureAwait(true);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
                return;
            _searchText = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    /// <summary>2026-10-19, этап О8: плашка ошибки загрузки — только когда есть текст.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    public ClientRow? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (ReferenceEquals(_selectedClient, value))
                return;
            _selectedClient = value;
            _editName = value?.FullName ?? "";
            _editPhone = value?.Phone ?? "";
            _editEmail = value?.Email ?? "";
            _editAddress = value?.Address ?? "";
            CardErrorMessage = "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditName));
            OnPropertyChanged(nameof(EditPhone));
            OnPropertyChanged(nameof(EditEmail));
            OnPropertyChanged(nameof(EditAddress));
            OnPropertyChanged(nameof(HasSelectedClient));
            OnPropertyChanged(nameof(CardTitle));
            OnPropertyChanged(nameof(CardSubtitle));
            UpdateCardButtonStates();
            // 2026-10-19, этап О8: долг клиента в карточке; подсказка Telegram прошлого клиента — убрать.
            UpdateCardDebt();
            UpdateCardDocs(); // 2026-10-21: документы клиента в карточке (у владельца)
            if (TelegramInviteHint != null)
                TelegramInviteHint.IsVisible = false;
            _ = LoadClientPurchasesAsync(value);
        }
    }

    public bool HasSelectedClient => _selectedClient != null;

    /// <summary>2026-10-19, этап О8: шапка карточки — имя клиента и с какого числа он в базе.</summary>
    public string CardTitle => _selectedClient?.FullName is { Length: > 0 } name ? name : Tr.T("Карточка клиента", "Кардардын карточкасы", "Client card", "Müşteri kartı", "Mijoz kartasi");

    public string CardSubtitle => _selectedClient is not { } c ? ""
        : string.IsNullOrEmpty(c.CreatedAtDisplay)
            ? Tr.T("Клиент магазина", "Дүкөндүн кардары", "Shop client", "Mağaza müşterisi", "Do'kon mijozi")
            : Tr.T($"В базе с {c.CreatedAtDisplay}", $"Базада {c.CreatedAtDisplay} баштап", $"In the list since {c.CreatedAtDisplay}",
                $"{c.CreatedAtDisplay} tarihinden beri kayıtlı", $"{c.CreatedAtDisplay} dan beri bazada");

    /// <summary>Покупки выбранного клиента — дата и сумма. Рядом с бонусным балансом это даёт
    /// кассиру ответ на живой вопрос «а он вообще у нас покупает и на сколько», не открывая
    /// отдельно «Продажи» и не фильтруя их руками.</summary>
    public ObservableCollection<ClientPurchaseRow> ClientPurchases { get; } = new();

    private string _purchasesSummary = "";

    public string PurchasesSummary
    {
        get => _purchasesSummary;
        private set { _purchasesSummary = value; OnPropertyChanged(); }
    }

    public bool HasClientPurchases => ClientPurchases.Count > 0;

    private CancellationTokenSource? _purchasesCts;

    private async Task LoadClientPurchasesAsync(ClientRow? client)
    {
        _purchasesCts?.Cancel();
        ClientPurchases.Clear();
        OnPropertyChanged(nameof(HasClientPurchases));

        if (client is null || string.IsNullOrWhiteSpace(client.Id))
        {
            PurchasesSummary = "";
            return;
        }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _purchasesCts = cts;
        PurchasesSummary = Tr.T("Загружаем покупки…", "Сатып алуулар жүктөлүүдө…", "Loading purchases…", "Alışverişler yükleniyor…", "Xaridlar yuklanmoqda…");

        try
        {
            var sales = await App.SalesApi
                .PosSalesByClientAsync(client.Id, maxPages: 3, cts.Token)
                .ConfigureAwait(true);

            // Клиент мог смениться, пока шёл запрос.
            if (!ReferenceEquals(_selectedClient, client))
                return;

            decimal total = 0;
            foreach (var sale in sales)
            {
                // Поля продажи взяты из реального ответа сервера: created_at (строка ISO) и
                // total (строка или число, в зависимости от сериализатора).
                var whenText = TryGetString(sale, "created_at");
                var hasDate = DateTimeOffset.TryParse(
                    whenText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when);

                decimal amount = 0m;
                if (sale.ValueKind == JsonValueKind.Object && sale.TryGetProperty("total", out var totalEl))
                {
                    amount = totalEl.ValueKind switch
                    {
                        JsonValueKind.Number => totalEl.TryGetDecimal(out var dn) ? dn : 0m,
                        JsonValueKind.String => decimal.TryParse(
                            totalEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds) ? ds : 0m,
                        _ => 0m,
                    };
                }

                total += amount;
                ClientPurchases.Add(new ClientPurchaseRow
                {
                    DateDisplay = hasDate
                        ? when.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                        : "—",
                    AmountDisplay = $"{MoneyText.Amount(amount)} {Som}", // 2026-10-20: разделитель тысяч, как везде
                });
            }

            // 2026-10-20, аудит программы владельца: «5115,00 сом» без разделителя тысяч — общий денежный формат (MoneyText).
            var totalText = MoneyText.Amount(total);
            PurchasesSummary = ClientPurchases.Count == 0
                ? Tr.T("Покупок пока нет.", "Азырынча сатып алуулар жок.", "No purchases yet.", "Henüz alışveriş yok.", "Hali xaridlar yo'q.")
                : Tr.T($"Покупок: {ClientPurchases.Count} · на сумму {totalText} сом",
                    $"Сатып алуулар: {ClientPurchases.Count} · суммасы {totalText} сом",
                    $"Purchases: {ClientPurchases.Count} · total {totalText} som",
                    $"Alışveriş: {ClientPurchases.Count} · toplam {totalText} som",
                    $"Xaridlar: {ClientPurchases.Count} · jami {totalText} so'm");
            OnPropertyChanged(nameof(HasClientPurchases));
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_selectedClient, client))
                PurchasesSummary = Tr.T("Не удалось загрузить покупки.", "Сатып алууларды жүктөө мүмкүн болгон жок.", "Could not load purchases.", "Alışverişler yüklenemedi.", "Xaridlarni yuklab bo'lmadi.");
            PosLogger.Log($"Client purchases load failed: {ex.Message}", "WARNING");
        }
    }

    public string EditName
    {
        get => _editName;
        set
        {
            _editName = value;
            OnPropertyChanged();
            UpdateCardButtonStates();
        }
    }

    public string EditPhone
    {
        get => _editPhone;
        set
        {
            _editPhone = value;
            OnPropertyChanged();
            UpdateCardButtonStates();
        }
    }

    /// <summary>Адрес клиента — необязательное поле, полезно при продаже в долг.</summary>
    public string EditAddress
    {
        get => _editAddress;
        set { _editAddress = value ?? ""; OnPropertyChanged(); }
    }

    public string EditEmail
    {
        get => _editEmail;
        set { _editEmail = value; OnPropertyChanged(); }
    }

    public bool IsCardBusy
    {
        get => _isCardBusy;
        private set
        {
            _isCardBusy = value;
            OnPropertyChanged();
            UpdateCardButtonStates();
        }
    }

    /// <summary>Выставляет IsEnabled кнопок карточки напрямую, в обход data-binding: в этом
    /// окне уже дважды было замечено, что IsEnabled, управляемый через Command.CanExecute или
    /// через обычный property-binding, не переоценивается корректно (кнопка залипает
    /// недоступной, хотя условие явно выполнено) — прямое присваивание из code-behind не
    /// зависит от этого механизма и гарантированно отражает актуальное состояние.</summary>
    private void UpdateCardButtonStates()
    {
        if (SaveClientButton != null)
            SaveClientButton.IsEnabled = CanSaveClient();
        if (DeleteClientButton != null)
            DeleteClientButton.IsEnabled = !_isCardBusy && _selectedClient != null;
    }

    public string CardErrorMessage
    {
        get => _cardErrorMessage;
        private set { _cardErrorMessage = value; OnPropertyChanged(); }
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e) =>
        await LoadClientsAsync().ConfigureAwait(true);

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): в шапке окна были только
    /// значок, название «Клиенты» и кнопки «свернуть» / ✕ — всё это уже есть над разделом, поэтому
    /// шапки нет целиком. ✕ карточки клиента остаётся: это кнопка карточки, а не окна.</summary>
    public void AsOwnerSection()
    {
        WindowHeaderBorder.IsVisible = false;
        RootGrid.Margin = OwnerSectionLayout.Margin;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        base.OnClosed(e);
    }

    private async Task LoadClientsAsync()
    {
        _loadCts?.Cancel();

        // Список покупок клиента грузится по своему токену с таймаутом 12 с. Без отмены при
        // закрытии он продолжал писать в коллекции закрытого окна, а каждый неосвобождённый
        // источник держал запись в системной очереди таймеров.
        _purchasesCts?.Cancel();
        _purchasesCts?.Dispose();
        _purchasesCts = null;
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        _isLoading = true;
        LoadingBar.IsVisible = true;
        if (RefreshButton != null)
            RefreshButton.IsEnabled = false;
        ErrorMessage = "";
        UpdateEmptyState();
        // 2026-10-19, этап О8: долги грузятся параллельно со списком (один запрос, как у «Сводки»), а не по клиенту.
        _ = LoadDebtsAsync();
        try
        {
            var raw = await _clientsApi.GetClientsAsync(null, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;

            _allClients.Clear();
            _allClients.AddRange(raw
                .Where(el => TryGetString(el, "type") is null or "client")
                .Select(ToClientRow)
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .OrderByDescending(c => c.CreatedAt));

            _clientsLoaded = true;
            ApplyDebtsToRows();
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer refresh
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось загрузить клиентов: ", "Клиенттерди жүктөө мүмкүн болгон жок: ", "Could not load clients: ", "Müşteriler yüklenemedi: ", "Mijozlarni yuklab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Clients load failed: {ex}", "WARNING");
        }
        finally
        {
            _isLoading = false;
            LoadingBar.IsVisible = false;
            if (RefreshButton != null)
                RefreshButton.IsEnabled = true;
            UpdateEmptyState();
        }
    }

    /// <summary>2026-10-19, этап О8: окно «Новый клиент» (ModalShell). Сохранение — прежний запрос CreateClientAsync.</summary>
    internal Window CreateNewClientDialog() => new NurMarketKassa.AvaloniaHost.Views.Dialogs.NewClientDialog(AddClientAsync, _allClients.ToList(),
        pickExisting: row => { _searchText = ""; OnPropertyChanged(nameof(SearchText)); ApplyFilter(); SelectedClient = _allClients.FirstOrDefault(c => c.Id == row.Id); }); // 2026-10-23: дубль телефона — открыть существующего

    /// <summary>Добавляет клиента на сервере (раньше — форма над списком). Удалось — клиент первым в списке и подсвечен
    /// («новый»), поиск и «Только должники» сброшены, чтобы его было видно; вернёт null. Не удалось — текст ошибки для окна.</summary>
    private async Task<string?> AddClientAsync(string name, string phone, string email, string address)
    {
        try
        {
            var created = await _clientsApi
                .CreateClientAsync(name, phone, email, address)
                .ConfigureAwait(true);

            var row = ToClientRow(created);
            if (string.IsNullOrWhiteSpace(row.Id))
                return Tr.T("Не удалось добавить клиента.", "Клиентти кошуу мүмкүн болгон жок.", "Could not add the client.", "Müşteri eklenemedi.", "Mijozni qo'shib bo'lmadi.");

            foreach (var other in _allClients)
                other.IsNew = false;
            row.IsNew = true;
            _allClients.Insert(0, row);
            _clientsLoaded = true;
            _searchText = "";
            OnPropertyChanged(nameof(SearchText));
            _onlyDebtors = false;
            _sort = "new";
            UpdateFilterButtons();
            ApplyDebtsToRows();
            ApplyFilter();
            ClientsScroll?.ScrollToHome();
            return null;
        }
        catch (ApiException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Client create failed: {ex}", "WARNING");
            return Tr.T("Не удалось добавить клиента: ", "Клиентти кошуу мүмкүн болгон жок: ", "Could not add the client: ", "Müşteri eklenemedi: ", "Mijozni qo'shib bo'lmadi: ") + ex.Message;
        }
    }

    private bool CanSaveClient() =>
        !_isCardBusy
        && _selectedClient != null
        && !string.IsNullOrWhiteSpace(_editName)
        && !string.IsNullOrWhiteSpace(_editPhone);

    private async void SaveClientButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanSaveClient())
            return;
        await SaveClientAsync().ConfigureAwait(true);
    }

    private async void DeleteClientButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isCardBusy || _selectedClient == null)
            return;
        await DeleteClientAsync().ConfigureAwait(true);
    }

    private async void AddClientButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await CreateNewClientDialog().ShowDialog(this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно «Новый клиент» не открылось: {ex}", "WARNING");
        }
    }

    private async Task SaveClientAsync()
    {
        var client = _selectedClient;
        if (client == null)
            return;

        IsCardBusy = true;
        CardErrorMessage = "";
        try
        {
            var updated = await _clientsApi
                .UpdateClientAsync(client.Id, _editName, _editPhone, _editEmail, _editAddress)
                .ConfigureAwait(true);

            var row = ToClientRow(updated);
            if (string.IsNullOrWhiteSpace(row.Id))
                row = new ClientRow { Id = client.Id, FullName = _editName.Trim(), Phone = _editPhone.Trim(), Email = (_editEmail ?? "").Trim(), Address = (_editAddress ?? "").Trim(), CreatedAt = client.CreatedAt, CreatedAtDisplay = client.CreatedAtDisplay };

            var index = _allClients.FindIndex(c => c.Id == client.Id);
            if (index >= 0)
                _allClients[index] = row;

            ApplyDebtsToRows();
            ApplyFilter();
            SelectedClient = null;
        }
        catch (ApiException ex)
        {
            CardErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            CardErrorMessage = Tr.T("Не удалось сохранить клиента: ", "Клиентти сактоо мүмкүн болгон жок: ", "Could not save the client: ", "Müşteri kaydedilemedi: ", "Mijozni saqlab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Client update failed: {ex}", "WARNING");
        }
        finally
        {
            IsCardBusy = false;
        }
    }

    private async Task DeleteClientAsync()
    {
        var client = _selectedClient;
        if (client == null)
            return;

        var confirmed = NurMarketKassa.AvaloniaHost.Services.PosDialogs.ConfirmYesNo(
            this,
            Tr.T($"Удалить клиента «{client.FullName}»?", $"«{client.FullName}» клиентин өчүрөсүзбү?", $"Delete client “{client.FullName}”?", $"«{client.FullName}» müşterisi silinsin mi?", $"«{client.FullName}» mijozini o'chirasizmi?"),
            // 2026-10-22 (модалки, пакет 2): глагол и красная кнопка вместо «Да/Нет».
            confirmText: Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
            danger: true);
        if (!confirmed)
            return;

        IsCardBusy = true;
        CardErrorMessage = "";
        try
        {
            await _clientsApi.DeleteClientAsync(client.Id).ConfigureAwait(true);
            _allClients.RemoveAll(c => c.Id == client.Id);
            ApplyFilter();
            SelectedClient = null;
        }
        catch (ApiException ex)
        {
            CardErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            CardErrorMessage = Tr.T("Не удалось удалить клиента: ", "Клиентти өчүрүү мүмкүн болгон жок: ", "Could not delete the client: ", "Müşteri silinemedi: ", "Mijozni o'chirib bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Client delete failed: {ex}", "WARNING");
        }
        finally
        {
            IsCardBusy = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredClients.Clear();
        var query = _searchText?.Trim() ?? "";
        // 2026-10-19, этап О8: телефон ищется и по цифрам («555123» находит «+996 555 123 456»), как в «Долгах клиентов».
        var digits = new string(query.Where(char.IsDigit).ToArray());
        IEnumerable<ClientRow> source = _allClients;
        if (query.Length > 0)
            source = source.Where(c =>
                SearchMatch.Matches(query, c.FullName, c.Phone) // 2026-10-29: единый поиск
                || (digits.Length >= 3 && new string(c.Phone.Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)));
        if (_onlyDebtors)
            source = source.Where(c => c.HasDebt);
        source = _sort switch
        {
            "name" => source.OrderBy(c => c.FullName, StringComparer.CurrentCultureIgnoreCase),
            "debt" => source.OrderByDescending(c => c.Debt).ThenBy(c => c.FullName, StringComparer.CurrentCultureIgnoreCase),
            _ => source.OrderByDescending(c => c.IsNew).ThenByDescending(c => c.CreatedAt),
        };

        foreach (var row in source)
            FilteredClients.Add(row);
        UpdateKpis();
        UpdateEmptyState();
    }

    private static ClientRow ToClientRow(JsonElement element)
    {
        var createdAt = TryGetString(element, "created_at");
        DateTimeOffset.TryParse(
            createdAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created);

        var id = TryGetString(element, "id") ?? "";
        // 2026-10-05: бонусы на сервере NurCRM — bonus_balance клиента; нет поля (старый сервер) — локальный баланс.
        var loyaltyBalance = double.TryParse(TryGetString(element, "bonus_balance"), NumberStyles.Any, CultureInfo.InvariantCulture, out var serverBonus)
            ? serverBonus
            : string.IsNullOrEmpty(id) ? 0 : ClientLoyaltyStore.GetBalance(id);

        return new ClientRow
        {
            Id = id,
            FullName = TryGetString(element, "full_name") ?? "",
            Phone = TryGetString(element, "phone") ?? "",
            Email = TryGetString(element, "email") ?? "",
            Address = TryGetString(element, "address") ?? "",
            CreatedAt = created,
            CreatedAtDisplay = created == default ? "" : created.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            LoyaltyBalance = loyaltyBalance,
            LoyaltyBalanceDisplay = $"{loyaltyBalance:0.##} {Som}",
        };
    }

    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // ── 2026-10-19, этап О8: тексты, плитки, порядок и фильтр, пустое состояние ──

    private void ApplyTexts()
    {
        SortNewButton.Content = Tr.T("Новые", "Жаңылар", "Newest", "Yeniler", "Yangilar");
        SortNameButton.Content = Tr.T("По имени", "Аты боюнча", "By name", "Ada göre", "Ism bo'yicha");
        SortDebtButton.Content = Tr.T("По долгу", "Карызы боюнча", "By debt", "Borca göre", "Qarz bo'yicha");
        OnlyDebtorsButton.Content = Tr.T("Только должники", "Карыздарлар гана", "Debtors only", "Sadece borçlular", "Faqat qarzdorlar");
        foreach (var b in new[] { SortNewButton, SortNameButton, SortDebtButton, OnlyDebtorsButton, RefreshButton, AddClientButton })
            Avalonia.Automation.AutomationProperties.SetName(b, b.Content as string ?? "");
        HeadDebt.Text = Tr.T("Долг, сом", "Карыз, сом", "Debt, som", "Borç, som", "Qarz, so'm");
        HeadBonus.Text = Tr.T("Бонусы, сом", "Бонустар, сом", "Bonuses, som", "Bonuslar, som", "Bonuslar, so'm");
        KpiClientsLabel.Text = Tr.T("Клиентов", "Кардарлар", "Clients", "Müşteriler", "Mijozlar");
        KpiDebtorsLabel.Text = Tr.T("Должников", "Карыздарлар", "Debtors", "Borçlular", "Qarzdorlar");
        KpiDebtLabel.Text = Tr.T("Вам должны", "Сизге карыз", "You are owed", "Size borçlu", "Sizga qarz");
        KpiBonusLabel.Text = Tr.T("Бонусы на счетах", "Эсептердеги бонустар", "Bonuses on accounts", "Hesaplardaki bonuslar", "Hisoblardagi bonuslar");
        CardNameLabel.Text = Tr.T("Имя / ФИО *", "Аты / аты-жөнү *", "Name *", "Ad soyad *", "Ism / F.I.Sh. *");
        CardPhoneLabel.Text = Tr.T("Телефон *", "Телефон *", "Phone *", "Telefon *", "Telefon *");
        CancelCardButton.Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
        Avalonia.Automation.AutomationProperties.SetName(CloseCardButton, Tr.T("Закрыть карточку", "Карточканы жабуу", "Close the card", "Kartı kapat", "Kartani yopish"));
        UpdateFilterButtons();
        UpdateKpis();
    }

    /// <summary>Выбранный порядок — тёмно-синий сегмент; «Только должники» — тёмно-синий чип, когда включён.</summary>
    private void UpdateFilterButtons()
    {
        UiKit.SetActive(SortNewButton, _sort == "new");
        UiKit.SetActive(SortNameButton, _sort == "name");
        UiKit.SetActive(SortDebtButton, _sort == "debt");
        UiKit.SetActive(OnlyDebtorsButton, _onlyDebtors);
    }

    private void SetSort(string sort)
    {
        _sort = sort;
        UpdateFilterButtons();
        ApplyFilter();
    }

    private void SortNew_Click(object? sender, RoutedEventArgs e) => SetSort("new");

    private void SortName_Click(object? sender, RoutedEventArgs e) => SetSort("name");

    private void SortDebt_Click(object? sender, RoutedEventArgs e) => SetSort("debt");

    /// <summary>Фильтр «Только должники» (для снимков и автоматики — тоже свойство).</summary>
    public bool OnlyDebtors
    {
        get => _onlyDebtors;
        set
        {
            _onlyDebtors = value;
            UpdateFilterButtons();
            ApplyFilter();
        }
    }

    private void OnlyDebtors_Click(object? sender, RoutedEventArgs e) => OnlyDebtors = !_onlyDebtors;

    /// <summary>Строка списка — кнопка: щелчок, Enter/Пробел и UIA открывают карточку клиента.</summary>
    private void ClientRow_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ClientRow row)
            SelectedClient = row;
    }

    /// <summary>Enter в поиске — карточка первого найденного клиента (нашёлся один — сразу он).</summary>
    private void SearchBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter || FilteredClients.Count == 0)
            return;
        e.Handled = true;
        SelectedClient = FilteredClients[0];
    }

    /// <summary>Плитки: клиентов (при поиске — «найдено из всего»), должников, сколько должны (красным, только если есть),
    /// бонусы на счетах. Долги — из <see cref="LoadDebtsAsync"/>; тариф без долгов — их плиток нет.</summary>
    private void UpdateKpis()
    {
        if (KpiClientsValue == null)
            return;
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var total = _allClients.Count;
        var shown = FilteredClients.Count;
        KpiClientsValue.Text = !_clientsLoaded ? "…"
            : shown == total ? total.ToString("N0", ru)
            : Tr.T($"{shown:N0} из {total:N0}", $"{total:N0} ичинен {shown:N0}", $"{shown:N0} of {total:N0}", $"{total:N0} içinden {shown:N0}", $"{total:N0} dan {shown:N0}");
        KpiBonusValue.Text = !_clientsLoaded ? "…" : _allClients.Sum(c => c.LoyaltyBalance).ToString("#,0.##", ru) + " " + Som;
        var debts = TariffGate.CanUseDebts;
        KpiDebtorsTile.IsVisible = debts;
        KpiDebtTile.IsVisible = debts;
        // 2026-10-20: проверка фильтров — на тарифе без долгов долги не загружаются (Debt = 0 у всех): «Только должники» давал
        // пустой список с «Все клиенты рассчитались», «По долгу» сортировал как «По имени». Без долгов этих кнопок нет.
        OnlyDebtorsButton.IsVisible = debts;
        SortDebtButton.IsVisible = debts;
        if (!debts)
            return;
        UpdateDebtKpis(ru);
    }

    /// <summary>Пустой список — что это значит и что сделать: клиентов ещё нет / никого не нашли / должников нет.</summary>
    private void UpdateEmptyState()
    {
        if (EmptyBox == null)
            return;
        var empty = _clientsLoaded && FilteredClients.Count == 0 && !_isLoading;
        EmptyBox.IsVisible = empty;
        if (!empty)
            return;
        if (_allClients.Count == 0)
        {
            EmptyTitle.Text = Tr.T("Клиентов пока нет", "Азырынча кардарлар жок", "No clients yet", "Henüz müşteri yok", "Hozircha mijozlar yo'q");
            EmptyText.Text = Tr.T("Нажмите «+ Клиент», чтобы добавить первого. Клиента выбирают при продаже — для бонусов и продажи в долг; здесь видно, кто сколько должен.",
                "Биринчисин кошуу үчүн «+ Кардар» баскычын басыңыз. Кардарды сатууда тандашат — бонустар жана карызга сатуу үчүн; бул жерде ким канча карыз экени көрүнөт.",
                "Press “+ Client” to add the first one. A client is chosen at checkout — for bonuses and selling on credit; here you see who owes how much.",
                "İlkini eklemek için «+ Müşteri»ye basın. Müşteri satışta seçilir — bonuslar ve veresiye satış için; burada kimin ne kadar borçlu olduğu görünür.",
                "Birinchisini qo'shish uchun «+ Mijoz» tugmasini bosing. Mijoz sotuvda tanlanadi — bonuslar va qarzga sotish uchun; bu yerda kim qancha qarzligi ko'rinadi.");
        }
        else if (_onlyDebtors && string.IsNullOrWhiteSpace(_searchText) && !_debtsLoaded)
        {
            // 2026-10-20: проверка фильтров — долги ещё грузятся или не загрузились: не «Все рассчитались», а что происходит.
            EmptyTitle.Text = _debtsFailed
                ? Tr.T("Долги не загружены", "Карыздар жүктөлгөн жок", "Debts not loaded", "Borçlar yüklenmedi", "Qarzlar yuklanmadi")
                : Tr.T("Загружаю долги…", "Карыздар жүктөлүүдө…", "Loading debts…", "Borçlar yükleniyor…", "Qarzlar yuklanmoqda…");
            EmptyText.Text = _debtsFailed
                ? Tr.T("Нет связи с сервером. Нажмите «Обновить», чтобы попробовать ещё раз.", "Сервер менен байланыш жок. Кайра аракет кылуу үчүн «Жаңыртуу» басыңыз.",
                    "No connection to the server. Press “Refresh” to try again.", "Sunucuyla bağlantı yok. Tekrar denemek için «Yenile»ye basın.",
                    "Server bilan aloqa yo'q. Qayta urinish uchun «Yangilash»ni bosing.")
                : Tr.T("Список должников появится через несколько секунд.", "Карыздарлардын тизмеси бир нече секунддан кийин чыгат.",
                    "The list of debtors will appear in a few seconds.", "Borçlu listesi birkaç saniye içinde görünecek.", "Qarzdorlar ro'yxati bir necha soniyada chiqadi.");
        }
        else if (_onlyDebtors && string.IsNullOrWhiteSpace(_searchText))
        {
            EmptyTitle.Text = Tr.T("Должников нет", "Карыздарлар жок", "No debtors", "Borçlu yok", "Qarzdorlar yo'q");
            EmptyText.Text = Tr.T("Все клиенты рассчитались. Нажмите «Только должники» ещё раз, чтобы увидеть всех.",
                "Бардык кардарлар эсептешти. Баарын көрүү үчүн «Карыздарлар гана» дегенди дагы бир жолу басыңыз.",
                "Every client has paid. Press “Debtors only” again to see everyone.",
                "Tüm müşteriler ödedi. Herkesi görmek için «Sadece borçlular»a tekrar basın.",
                "Barcha mijozlar to'lagan. Hammasini ko'rish uchun «Faqat qarzdorlar»ni yana bosing.");
        }
        else
        {
            EmptyTitle.Text = Tr.T("Никого не нашли", "Эч ким табылган жок", "Nobody found", "Kimse bulunamadı", "Hech kim topilmadi");
            EmptyText.Text = Tr.T("Проверьте имя или номер телефона (можно вводить только цифры) или очистите поиск.",
                "Атын же телефон номерин текшериңиз (сандарды гана жазса болот) же издөөнү тазалаңыз.",
                "Check the name or phone number (digits alone are fine) or clear the search.",
                "Adı veya telefon numarasını kontrol edin (yalnızca rakam da olur) ya da aramayı temizleyin.",
                "Ism yoki telefon raqamini tekshiring (faqat raqamlar ham bo'ladi) yoki qidiruvni tozalang.")
                + (_onlyDebtors ? " " + Tr.T("Включён фильтр «Только должники».", "«Карыздарлар гана» чыпкасы күйгүзүлгөн.", "The “Debtors only” filter is on.",
                    "«Sadece borçlular» filtresi açık.", "«Faqat qarzdorlar» filtri yoqilgan.") : "");
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ClientRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Email { get; init; } = "";

    /// <summary>Адрес клиента с сервера (поле «address»). Необязателен; нужен прежде всего
    /// для долгов — по нему можно найти покупателя, когда телефон не отвечает.</summary>
    public string Address { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedAtDisplay { get; init; } = "";

    /// <summary>Локальный бонусный баланс на этой кассе (AI-фичи 2026-09-04) — см. ClientLoyaltyStore.</summary>
    public double LoyaltyBalance { get; init; }
    public string LoyaltyBalanceDisplay { get; init; } = "";

    // 2026-10-19, этап О8: только для вида списка — долг (из быстрого списка долгов DebtsView), «новый», вторая строка.

    /// <summary>Остаток долга клиента, сом (0 — долга нет).</summary>
    public double Debt { get; set; }
    /// <summary>Чеков в долг и самый старый из них — для карточки.</summary>
    public int DebtSalesCount { get; set; }
    public DateTime? DebtSince { get; set; }
    /// <summary>Долги ещё не получены (или тариф без долгов) — в столбце пусто, а не «—».</summary>
    public bool DebtUnknown { get; set; } = true;
    /// <summary>Только что добавлен в этом окне — подсвечен и с меткой «новый».</summary>
    public bool IsNew { get; set; }

    public bool HasDebt => Debt > 0.005;
    public string DebtText => DebtUnknown ? "" : HasDebt ? Debt.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) : "—";
    public string BonusText => LoyaltyBalance > 0.005 ? LoyaltyBalance.ToString("#,0.##", CultureInfo.GetCultureInfo("ru-RU")) : "—";
    public string BonusCardText => LoyaltyBalance.ToString("#,0.##", CultureInfo.GetCultureInfo("ru-RU")) + " " + Tr.T("сом", "сом", "som", "som", "so'm");
    public string PhoneDisplay => string.IsNullOrWhiteSpace(Phone) ? "—" : Phone;
    public string SubLine => string.Join(" · ", new[] { Address, Email }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public bool HasSecondLine => IsNew || SubLine.Length > 0;
    public string NewBadgeText => Tr.T("новый", "жаңы", "new", "yeni", "yangi");
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>2026-10-19, этап О8: долг обновился (оплата, событие другой кассы) — строка перерисовывается на месте, список не
    /// пересобирается и не прыгает к началу.</summary>
    public void NotifyDebt()
    {
        foreach (var name in new[] { nameof(Debt), nameof(HasDebt), nameof(DebtText), nameof(AutomationName) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Имя строки для экранного диктора и автоматики (UIA): имя, телефон и долг.</summary>
    public string AutomationName => string.Join(", ", new[] { FullName, Phone, HasDebt ? Tr.T("долг ", "карыз ", "debt ", "borç ", "qarz ") + DebtText : "" }
        .Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>Одна покупка клиента для его карточки: когда и на сколько.</summary>
public sealed class ClientPurchaseRow
{
    public string DateDisplay { get; init; } = "";
    public string AmountDisplay { get; init; } = "";
}
