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
public partial class ClientsWindow : Window, INotifyPropertyChanged
{
    private readonly IClientsApiService _clientsApi;
    private readonly List<ClientRow> _allClients = new();

    private string _searchText = "";
    private string _newClientName = "";
    private string _newClientPhone = "";
    private string _newClientEmail = "";
    private bool _isLoading;
    private bool _isSaving;
    private string _errorMessage = "";
    private CancellationTokenSource? _loadCts;

    private ClientRow? _selectedClient;
    private string _editName = "";
    private string _editPhone = "";
    private string _editEmail = "";
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

        UpdateCardButtonStates();
        UpdateAddButtonState();
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
            TelegramInviteHint.Text =
                "Сначала подключите бота в «Настройки → Операции»: имя бота касса узнаёт при определении получателя.";
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(link).ConfigureAwait(true);

        TelegramInviteHint.Text = $"Ссылка скопирована: {link}\n"
            + "Отправьте её клиенту. После нажатия «Старт» он начнёт получать напоминания.";
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

    public string NewClientName
    {
        get => _newClientName;
        set
        {
            _newClientName = value;
            OnPropertyChanged();
            UpdateAddButtonState();
        }
    }

    public string NewClientPhone
    {
        get => _newClientPhone;
        set
        {
            _newClientPhone = value;
            OnPropertyChanged();
            UpdateAddButtonState();
        }
    }

    public string NewClientEmail
    {
        get => _newClientEmail;
        set { _newClientEmail = value; OnPropertyChanged(); }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            _isSaving = value;
            OnPropertyChanged();
            UpdateAddButtonState();
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public string CountText => $"Клиентов: {_allClients.Count}";

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
            CardErrorMessage = "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditName));
            OnPropertyChanged(nameof(EditPhone));
            OnPropertyChanged(nameof(EditEmail));
            OnPropertyChanged(nameof(HasSelectedClient));
            UpdateCardButtonStates();
            _ = LoadClientPurchasesAsync(value);
        }
    }

    public bool HasSelectedClient => _selectedClient != null;

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
        PurchasesSummary = "Загружаем покупки…";

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
                    AmountDisplay = $"{amount:0.00} сом",
                });
            }

            PurchasesSummary = ClientPurchases.Count == 0
                ? "Покупок пока нет."
                : $"Покупок: {ClientPurchases.Count} · на сумму {total:0.00} сом";
            OnPropertyChanged(nameof(HasClientPurchases));
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_selectedClient, client))
                PurchasesSummary = "Не удалось загрузить покупки.";
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

    private void UpdateAddButtonState()
    {
        if (AddClientButton != null)
            AddClientButton.IsEnabled = CanAddClient();
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
        if (RefreshButton != null)
            RefreshButton.IsEnabled = false;
        ErrorMessage = "";
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

            ApplyFilter();
            OnPropertyChanged(nameof(CountText));
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
            ErrorMessage = "Не удалось загрузить клиентов: " + ex.Message;
            PosLogger.Log($"Clients load failed: {ex}", "WARNING");
        }
        finally
        {
            _isLoading = false;
            if (RefreshButton != null)
                RefreshButton.IsEnabled = true;
        }
    }

    private bool CanAddClient() =>
        !_isSaving
        && !string.IsNullOrWhiteSpace(_newClientName)
        && !string.IsNullOrWhiteSpace(_newClientPhone);

    private async Task AddClientAsync()
    {
        IsSaving = true;
        ErrorMessage = "";
        try
        {
            var created = await _clientsApi
                .CreateClientAsync(_newClientName, _newClientPhone, _newClientEmail)
                .ConfigureAwait(true);

            var row = ToClientRow(created);
            if (string.IsNullOrWhiteSpace(row.Id))
            {
                ErrorMessage = "Не удалось добавить клиента.";
                return;
            }

            _allClients.Insert(0, row);
            ApplyFilter();
            OnPropertyChanged(nameof(CountText));

            NewClientName = "";
            NewClientPhone = "";
            NewClientEmail = "";
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Не удалось добавить клиента: " + ex.Message;
            PosLogger.Log($"Client create failed: {ex}", "WARNING");
        }
        finally
        {
            IsSaving = false;
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
        if (!CanAddClient())
            return;
        await AddClientAsync().ConfigureAwait(true);
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
                .UpdateClientAsync(client.Id, _editName, _editPhone, _editEmail)
                .ConfigureAwait(true);

            var row = ToClientRow(updated);
            if (string.IsNullOrWhiteSpace(row.Id))
                row = new ClientRow { Id = client.Id, FullName = _editName.Trim(), Phone = _editPhone.Trim(), Email = (_editEmail ?? "").Trim(), CreatedAt = client.CreatedAt, CreatedAtDisplay = client.CreatedAtDisplay };

            var index = _allClients.FindIndex(c => c.Id == client.Id);
            if (index >= 0)
                _allClients[index] = row;

            ApplyFilter();
            SelectedClient = null;
        }
        catch (ApiException ex)
        {
            CardErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            CardErrorMessage = "Не удалось сохранить клиента: " + ex.Message;
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
            $"Удалить клиента «{client.FullName}»?");
        if (!confirmed)
            return;

        IsCardBusy = true;
        CardErrorMessage = "";
        try
        {
            await _clientsApi.DeleteClientAsync(client.Id).ConfigureAwait(true);
            _allClients.RemoveAll(c => c.Id == client.Id);
            ApplyFilter();
            OnPropertyChanged(nameof(CountText));
            SelectedClient = null;
        }
        catch (ApiException ex)
        {
            CardErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            CardErrorMessage = "Не удалось удалить клиента: " + ex.Message;
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
        IEnumerable<ClientRow> source = _allClients;
        if (query.Length > 0)
            source = source.Where(c =>
                c.FullName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.Phone.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        foreach (var row in source)
            FilteredClients.Add(row);
    }

    private static ClientRow ToClientRow(JsonElement element)
    {
        var createdAt = TryGetString(element, "created_at");
        DateTimeOffset.TryParse(
            createdAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created);

        var id = TryGetString(element, "id") ?? "";
        var loyaltyBalance = string.IsNullOrEmpty(id) ? 0 : ClientLoyaltyStore.GetBalance(id);

        return new ClientRow
        {
            Id = id,
            FullName = TryGetString(element, "full_name") ?? "",
            Phone = TryGetString(element, "phone") ?? "",
            Email = TryGetString(element, "email") ?? "",
            CreatedAt = created,
            CreatedAtDisplay = created == default ? "" : created.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            LoyaltyBalance = loyaltyBalance,
            LoyaltyBalanceDisplay = $"{loyaltyBalance:0.##} сом",
        };
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ClientRow
{
    public string Id { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Email { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedAtDisplay { get; init; } = "";

    /// <summary>Локальный бонусный баланс на этой кассе (AI-фичи 2026-09-04) — см. ClientLoyaltyStore.</summary>
    public double LoyaltyBalance { get; init; }
    public string LoyaltyBalanceDisplay { get; init; } = "";
}

/// <summary>Одна покупка клиента для его карточки: когда и на сколько.</summary>
public sealed class ClientPurchaseRow
{
    public string DateDisplay { get; init; } = "";
    public string AmountDisplay { get; init; } = "";
}
